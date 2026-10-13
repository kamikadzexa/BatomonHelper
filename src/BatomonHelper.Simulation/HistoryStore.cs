using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace BatomonHelper.Simulation;

public sealed class HistoryStore : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _connectionString;
    private readonly Channel<RunRecord> _pending=Channel.CreateBounded<RunRecord>(new BoundedChannelOptions(128)
        { SingleReader=true, FullMode=BoundedChannelFullMode.Wait });
    private readonly Task _writer;
    private readonly int _recent,_best,_generations;
    private Exception? _failure;
    private long _enqueued,_committed;
    public HistoryStore(string directory,int recent=100,int best=20,int generations=20)
    {
        _directory=Path.GetFullPath(directory); Directory.CreateDirectory(_directory);
        _recent=recent; _best=best; _generations=generations;
        _connectionString=new SqliteConnectionStringBuilder { DataSource=Path.Combine(_directory,"index.db"),Pooling=false }.ToString();
        using var connection=Connect();
        using var command=connection.CreateCommand(); command.CommandText="""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, generation INTEGER, policy INTEGER,
                trainer TEXT, wins INTEGER, lives INTEGER, days INTEGER, success INTEGER, evaluation INTEGER,
                completed TEXT, rules_hash TEXT, snapshot_update INTEGER NOT NULL DEFAULT -1, evaluation_batch TEXT, pinned INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS recent_runs ON runs(completed DESC);
            CREATE INDEX IF NOT EXISTS generation_runs ON runs(generation,wins DESC,lives DESC,days,id);
            """; command.ExecuteNonQuery();
        _writer=Task.Run(WriteLoop);
    }
    private SqliteConnection Connect() { var c=new SqliteConnection(_connectionString); c.Open(); return c; }
    public async ValueTask AppendAsync(RunRecord record,CancellationToken token=default)
    {
        if(_failure is not null) throw new IOException("History writer failed.",_failure);
        // Freeze before publishing: running environments never share references with the saved record.
        var frozen=StateSnapshots.Copy(record);
        await _pending.Writer.WriteAsync(frozen,token);
        Interlocked.Increment(ref _enqueued);
    }
    private async Task WriteLoop()
    {
        try
        {
            while(await _pending.Reader.WaitToReadAsync())
            {
                var batch=new List<RunRecord>();while(batch.Count<32&&_pending.Reader.TryRead(out var next))batch.Add(next);
                using var batchConnection=Connect();using var batchTransaction=batchConnection.BeginTransaction();
                foreach(var record in batch)
                {
                using(var query=batchConnection.CreateCommand()){
                    query.Transaction=batchTransaction;
                    query.CommandText="SELECT pinned FROM runs WHERE id=$id";query.Parameters.AddWithValue("$id",record.Summary.RunId);
                    if(query.ExecuteScalar() is long pinned && pinned!=0)continue;
                }
                var path=RecordPath(record.Summary.RunId); var staged=path+".tmp";
                try
                {
                    await using(var file=File.Create(staged))
                    await using(var zip=new BrotliStream(file,CompressionLevel.Fastest))
                        await JsonSerializer.SerializeAsync(zip,record);
                    File.Move(staged,path,true);
                    using var cmd=batchConnection.CreateCommand();cmd.Transaction=batchTransaction;
                    cmd.CommandText="""
                        INSERT INTO runs(id,generation,policy,trainer,wins,lives,days,success,evaluation,completed,rules_hash,snapshot_update,evaluation_batch,pinned)
                        VALUES($id,$generation,$policy,$trainer,$wins,$lives,$days,$success,$evaluation,$completed,$hash,$snapshot,$batch,0)
                        ON CONFLICT(id) DO UPDATE SET generation=excluded.generation,policy=excluded.policy,trainer=excluded.trainer,
                            wins=excluded.wins,lives=excluded.lives,days=excluded.days,success=excluded.success,
                            evaluation=excluded.evaluation,completed=excluded.completed,rules_hash=excluded.rules_hash,
                            snapshot_update=excluded.snapshot_update,evaluation_batch=excluded.evaluation_batch;
                        """;
                    var s=record.Summary;
                    foreach(var (key,value) in new (string,object)[] {("id",s.RunId),("generation",s.Generation),("policy",s.Policy),("trainer",s.Trainer),
                        ("wins",s.Wins),("lives",s.TurnsRemaining),("days",s.Days),("success",s.Success),("evaluation",s.Evaluation),("completed",s.CompletedAt.ToString("O")),("hash",s.RulesHash)})
                        cmd.Parameters.AddWithValue("$"+key,value);
                    cmd.Parameters.AddWithValue("$snapshot",s.SnapshotUpdate);cmd.Parameters.AddWithValue("$batch",(object?)s.EvaluationBatch??DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                finally { if(File.Exists(staged)) File.Delete(staged); }
                }
                batchTransaction.Commit();Prune();Interlocked.Add(ref _committed,batch.Count);
            }
        }
        catch(Exception error) { _failure=error; _pending.Writer.TryComplete(error); throw; }
    }
    public RunSummary[] List(bool best=false,int? generation=null,int? policy=null,string? trainer=null,bool? success=null,bool? evaluation=null)
    {
        using var c=Connect(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT id,generation,policy,trainer,wins,lives,days,success,evaluation,completed,rules_hash,pinned,snapshot_update,evaluation_batch FROM runs WHERE ($generation IS NULL OR generation=$generation) AND ($policy IS NULL OR policy=$policy) " +
            "AND ($trainer IS NULL OR trainer=$trainer) AND ($success IS NULL OR success=$success) AND ($evaluation IS NULL OR evaluation=$evaluation) ORDER BY "+
            (best?"wins DESC,lives DESC,days,id":"completed DESC,id")+" LIMIT 1000";
        cmd.Parameters.AddWithValue("$generation",(object?)generation??DBNull.Value); cmd.Parameters.AddWithValue("$policy",(object?)policy??DBNull.Value);
        cmd.Parameters.AddWithValue("$trainer",(object?)trainer??DBNull.Value); cmd.Parameters.AddWithValue("$success",(object?)success??DBNull.Value);
        cmd.Parameters.AddWithValue("$evaluation",(object?)evaluation??DBNull.Value);
        using var reader=cmd.ExecuteReader(); var result=new List<RunSummary>();
        while(reader.Read()) result.Add(new(reader.GetString(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetString(3),reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6),
            reader.GetBoolean(7),reader.GetBoolean(8),DateTimeOffset.Parse(reader.GetString(9),System.Globalization.CultureInfo.InvariantCulture),reader.GetString(10),reader.GetBoolean(11),reader.GetInt32(12),reader.IsDBNull(13)?null:reader.GetString(13)));
        return result.ToArray();
    }
    public async Task<RunRecord> LoadAsync(string id,CancellationToken token=default)
    {
        await using var file=File.OpenRead(RecordPath(id)); await using var zip=new BrotliStream(file,CompressionMode.Decompress);
        var record=await JsonSerializer.DeserializeAsync<RunRecord>(zip,cancellationToken:token)??throw new InvalidDataException("Empty run record.");
        if(record.Summary.RunId!=id) throw new InvalidDataException("Run identity mismatch.");
        return record;
    }
    public void Pin(string id,bool pinned)
    {
        _=RecordPath(id); using var c=Connect(); using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE runs SET pinned=$p WHERE id=$id"; cmd.Parameters.AddWithValue("$p",pinned); cmd.Parameters.AddWithValue("$id",id);
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("This run is no longer in the retained history.");
    }
    public bool TogglePin(string id)
    {
        _=RecordPath(id);using var c=Connect();using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE runs SET pinned=1-pinned WHERE id=$id RETURNING pinned";cmd.Parameters.AddWithValue("$id",id);
        var value=cmd.ExecuteScalar()??throw new InvalidOperationException("This run is no longer in the retained history.");
        return Convert.ToBoolean(value);
    }
    public async Task FlushAsync(CancellationToken token=default)
    {
        var target=Interlocked.Read(ref _enqueued);
        while(Interlocked.Read(ref _committed)<target)
        {
            if(_failure is not null) throw new IOException("History writer failed.",_failure);
            await Task.Delay(10,token);
        }
    }
    private void Prune()
    {
        using var c=Connect(); using var transaction=c.BeginTransaction(); using var cmd=c.CreateCommand(); cmd.Transaction=transaction;
        cmd.CommandText="""
            SELECT id FROM runs WHERE pinned=0 AND id NOT IN (SELECT id FROM runs ORDER BY completed DESC,id LIMIT $recent)
            AND id NOT IN (SELECT id FROM (
                SELECT id,generation,ROW_NUMBER() OVER(PARTITION BY generation,evaluation ORDER BY wins DESC,lives DESC,days,id) AS ordinal FROM runs
                WHERE generation IN (SELECT DISTINCT generation FROM runs ORDER BY generation DESC LIMIT $generations)) WHERE ordinal<=$best);
            """;
        cmd.Parameters.AddWithValue("$recent",_recent); cmd.Parameters.AddWithValue("$generations",_generations); cmd.Parameters.AddWithValue("$best",_best);
        var removed=new List<string>(); using(var reader=cmd.ExecuteReader()) while(reader.Read()) removed.Add(reader.GetString(0));
        foreach(var id in removed)
        {
            using var delete=c.CreateCommand(); delete.Transaction=transaction; delete.CommandText="DELETE FROM runs WHERE id=$id AND pinned=0";
            delete.Parameters.AddWithValue("$id",id); delete.ExecuteNonQuery();
        }
        transaction.Commit(); foreach(var id in removed) File.Delete(RecordPath(id));
    }
    private string RecordPath(string id)
    {
        if(!Regex.IsMatch(id,"^[A-Za-z0-9_-]{1,160}$")) throw new ArgumentException("Invalid run ID.");
        return Path.Combine(_directory,id+".json.br");
    }
    public async ValueTask DisposeAsync() { _pending.Writer.TryComplete(); await _writer; }
}
