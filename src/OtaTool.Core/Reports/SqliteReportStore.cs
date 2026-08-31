using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;
using OtaTool.Core.Execution;
using OtaTool.Core.Models;

namespace OtaTool.Core.Reports;

public sealed class SqliteReportStore : ITaskSequenceStore
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly string _connectionString;

    public SqliteReportStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ota_sequences (id INTEGER PRIMARY KEY CHECK(id = 1), last_value INTEGER NOT NULL);
            INSERT OR IGNORE INTO ota_sequences(id, last_value) VALUES(1, 0);
            CREATE TABLE IF NOT EXISTS ota_reports (
                id TEXT PRIMARY KEY, created_at TEXT NOT NULL, mode TEXT NOT NULL, device_type TEXT NOT NULL,
                old_version TEXT NOT NULL, new_version TEXT NOT NULL, final_state TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ota_test_plan_reports (
                id TEXT PRIMARY KEY, created_at TEXT NOT NULL, mode TEXT NOT NULL, gateway_id TEXT NOT NULL,
                final_state TEXT NOT NULL, json TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> NextAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE ota_sequences SET last_value = last_value + 1 WHERE id = 1;";
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT last_value FROM ota_sequences WHERE id = 1;";
        var value = Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return value;
    }

    public async Task SaveAsync(OtaReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await InitializeAsync(cancellationToken);
        var json = JsonSerializer.Serialize(report, ReportJsonOptions);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ota_reports(id, created_at, mode, device_type, old_version, new_version, final_state, json)
            VALUES($id, $createdAt, $mode, $deviceType, $oldVersion, $newVersion, $finalState, $json)
            ON CONFLICT(id) DO UPDATE SET final_state=excluded.final_state, json=excluded.json;
            """;
        command.Parameters.AddWithValue("$id", report.Id.ToString());
        command.Parameters.AddWithValue("$createdAt", report.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$mode", report.Task.Mode.ToString());
        command.Parameters.AddWithValue("$deviceType", report.Task.DeviceType.ToString());
        command.Parameters.AddWithValue("$oldVersion", report.Task.OldVersion);
        command.Parameters.AddWithValue("$newVersion", report.Task.NewVersion);
        command.Parameters.AddWithValue("$finalState", report.FinalState.ToString());
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OtaReport>> LoadRecentAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM ota_reports ORDER BY created_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var reports = new List<OtaReport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var report = JsonSerializer.Deserialize<OtaReport>(reader.GetString(0), ReportJsonOptions);
            if (report is not null) reports.Add(report);
        }
        return reports;
    }

    public async Task DeleteAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ota_reports WHERE id = $id;";
        command.Parameters.AddWithValue("$id", reportId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SavePlanAsync(OtaTestPlanReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await InitializeAsync(cancellationToken);
        var json = JsonSerializer.Serialize(report, ReportJsonOptions);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ota_test_plan_reports(id, created_at, mode, gateway_id, final_state, json)
            VALUES($id, $createdAt, $mode, $gatewayId, $finalState, $json)
            ON CONFLICT(id) DO UPDATE SET final_state=excluded.final_state, json=excluded.json;
            """;
        command.Parameters.AddWithValue("$id", report.Id.ToString());
        command.Parameters.AddWithValue("$createdAt", report.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$mode", report.Plan.Mode.ToString());
        command.Parameters.AddWithValue("$gatewayId", report.Plan.GatewayId);
        command.Parameters.AddWithValue("$finalState", report.FinalState.ToString());
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OtaTestPlanReport>> LoadRecentPlansAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM ota_test_plan_reports ORDER BY created_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var reports = new List<OtaTestPlanReport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var report = JsonSerializer.Deserialize<OtaTestPlanReport>(reader.GetString(0), ReportJsonOptions);
            if (report is not null) reports.Add(report);
        }
        return reports;
    }

    public async Task DeletePlanAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ota_test_plan_reports WHERE id = $id;";
        command.Parameters.AddWithValue("$id", reportId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
