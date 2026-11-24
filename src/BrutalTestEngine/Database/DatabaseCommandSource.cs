using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Io;
using Microsoft.Data.Sqlite;

namespace BrutalTestEngine.Database;

/// <summary>A row the database watch observed.</summary>
/// <param name="Id">The row's identifier.</param>
/// <param name="Command">The command text stored on the row.</param>
public readonly record struct DatabaseRow(long Id, string Command);

/// <summary>
/// Watches a SQLite table for new commands and posts them to the bus.
/// </summary>
/// <remarks>
/// <para>
/// A database is the natural channel for out-of-band control: a live-ops tool, an admin page or another
/// service writes a row and the engine picks it up. Like every other source it produces commands rather than
/// side effects, so a change made in a database travels the same validated path as a key press.
/// </para>
/// <para>
/// The watch polls rather than using a trigger, because SQLite has no cross-process change notification: a
/// writer in another process can only be observed by re-reading. Polling a monotonic row id also makes the
/// watch restartable, since the high-water mark is all the state it needs.
/// </para>
/// <para>
/// Every database touch happens on the IO thread. Opening, reading and writing a database file all block, so
/// the polling thread posts an <see cref="IoJob"/> through the IO thread and waits for it, and never touches
/// a disk itself.
/// </para>
/// </remarks>
public sealed class DatabaseCommandSource : IDisposable
{
    private const int PollIntervalMilliseconds = 250;
    private const int MaxCommandsPerPoll = 64;

    private readonly CommandBus _bus;
    private readonly IoThread _io;
    private readonly string _connectionString;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _pollCompleted = new(false);
    private readonly ThreadMetrics _pollMetrics;
    private long _lastId;
    private long _commandsPosted;
    private long _polls;
    private long _failures;
    private string? _lastError;
    private volatile bool _disposed;

    /// <param name="bus">The command bus rows are posted to.</param>
    /// <param name="io">The IO thread that performs the blocking database work.</param>
    /// <param name="timers">Where this source records how long one poll cycle took.</param>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="table">Name of the table to watch.</param>
    public DatabaseCommandSource(
        CommandBus bus,
        IoThread io,
        ThreadTimerRegistry timers,
        string databasePath,
        string table = "commands"
    )
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        _bus = bus;
        _io = io;
        _pollMetrics = timers.Get(ThreadTimerRegistry.Keys.DatabasePoll);
        Table = table;

        // Read/write/create, so the engine's own verification can write rows and prove the path end to end.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        DatabasePath = databasePath;

        _thread = new Thread(Run)
        {
            Name = "DatabaseWatch",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The database file being watched.</summary>
    public string DatabasePath { get; }

    /// <summary>Name of the watched table.</summary>
    public string Table { get; }

    /// <summary>Number of commands posted to the bus from watched rows.</summary>
    public long CommandsPosted => Interlocked.Read(ref _commandsPosted);

    /// <summary>Number of completed poll cycles.</summary>
    public long PollCount => Interlocked.Read(ref _polls);

    /// <summary>Number of polls that threw, for example because another writer held the file.</summary>
    public long FailureCount => Interlocked.Read(ref _failures);

    /// <summary>The most recent error, if any.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// Creates the database and the watched table if they do not exist. Blocks on the IO thread, so callers
    /// use it at startup, not from a tick or a frame.
    /// </summary>
    public void Initialize() => RunOnIo(EnsureSchema);

    /// <summary>
    /// Appends a command row. Used by the engine's verification to exercise the path, and available to any
    /// caller that wants to enqueue work through the database.
    /// </summary>
    public void Append(string command) =>
        RunOnIo(connection =>
        {
            EnsureSchema(connection);

            using var insert = connection.CreateCommand();
            insert.CommandText = $"INSERT INTO {Table} (command) VALUES ($command);";
            insert.Parameters.AddWithValue("$command", command);
            insert.ExecuteNonQuery();
        });

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _pollCompleted.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _pollCompleted.Dispose();
        _cancellation.Dispose();
    }

    private void Run()
    {
        var token = _cancellation.Token;

        // Create the schema up front, so the file exists and is usable even if no writer ever appears.
        RunOnIo(EnsureSchema);

        while (!token.IsCancellationRequested)
        {
            var pollStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                RunOnIo(Poll);
            }
            finally
            {
                // Measured across the hand-off to the IO thread and back, because that round trip — not just
                // the SQL — is what the polling thread actually pays for.
                _pollMetrics.RecordSince(pollStartedAt);
            }

            if (token.WaitHandle.WaitOne(PollIntervalMilliseconds))
                break;
        }
    }

    /// <summary>
    /// Runs one unit of database work on the IO thread and waits for it to finish.
    /// </summary>
    private void RunOnIo(Action<SqliteConnection> work)
    {
        _pollCompleted.Reset();

        _io.Post(
            new IoJob(
                "database-poll",
                () =>
                {
                    try
                    {
                        using var connection = new SqliteConnection(_connectionString);
                        connection.Open();
                        work(connection);
                        Interlocked.Increment(ref _polls);
                        Volatile.Write(ref _lastError, null);
                    }
                    catch (SqliteException exception)
                    {
                        // A locked database is normal while another process writes, so it is reported rather
                        // than fatal: the next poll retries.
                        Interlocked.Increment(ref _failures);
                        Volatile.Write(ref _lastError, exception.Message);
                    }
                    finally
                    {
                        _pollCompleted.Set();
                    }
                }
            )
        );

        _pollCompleted.Wait();
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        using var create = connection.CreateCommand();
        create.CommandText =
            $"CREATE TABLE IF NOT EXISTS {Table} (id INTEGER PRIMARY KEY AUTOINCREMENT, command TEXT NOT NULL);";
        create.ExecuteNonQuery();
    }

    private void Poll(SqliteConnection connection)
    {
        EnsureSchema(connection);

        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT id, command FROM {Table} WHERE id > $lastId ORDER BY id LIMIT $limit;";
        select.Parameters.AddWithValue("$lastId", Interlocked.Read(ref _lastId));
        select.Parameters.AddWithValue("$limit", MaxCommandsPerPoll);

        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            var row = new DatabaseRow(reader.GetInt64(0), reader.GetString(1));

            // The high-water mark advances even for an empty row, so a malformed write cannot wedge the
            // watch by being read forever.
            Interlocked.Exchange(ref _lastId, row.Id);

            if (row.Command.Trim().Length == 0)
                continue;

            _bus.Post(CommandParser.Parse(row.Command, CommandSource.Database));
            Interlocked.Increment(ref _commandsPosted);
        }
    }
}
