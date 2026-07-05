using System.Data;
using Dapper;

namespace PasMigration.Data;

/// <summary>
/// Data-access seam for the <c>engagement</c> table. The only place engagement SQL lives.
///
/// Behavior note: <see cref="ListAsync"/> intentionally returns Dapper's dynamic rows rather than a
/// typed model. Route handlers serialize these directly, and the JSON must stay snake_case
/// (<c>customer_name</c>, <c>created_at</c>) to match the frontend's <c>Engagement</c> interface.
/// Introducing a PascalCase C# model here would change the wire shape to camelCase and break the
/// SPA — so the pilot preserves the exact existing shape. A typed model can be introduced later as
/// a deliberate, separately-verified change.
/// </summary>
public interface IEngagementRepository
{
    /// <summary>All engagements, newest first. Rows are dynamic (snake_case columns preserved).</summary>
    Task<IEnumerable<dynamic>> ListAsync(CancellationToken ct = default);

    /// <summary>Inserts an engagement and returns its new id.</summary>
    Task<Guid> CreateAsync(string name, string customerName, CancellationToken ct = default);
}

public sealed class EngagementRepository(IDbConnection db) : IEngagementRepository
{
    public async Task<IEnumerable<dynamic>> ListAsync(CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            "SELECT id, name, customer_name, status, created_at FROM engagement ORDER BY created_at DESC",
            cancellationToken: ct));

    public async Task<Guid> CreateAsync(string name, string customerName, CancellationToken ct = default) =>
        await db.ExecuteScalarAsync<Guid>(new CommandDefinition(
            @"INSERT INTO engagement (name, customer_name) VALUES (@Name, @CustomerName)
              RETURNING id",
            new { Name = name, CustomerName = customerName },
            cancellationToken: ct));
}
