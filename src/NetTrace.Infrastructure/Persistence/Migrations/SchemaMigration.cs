namespace NetTrace.Infrastructure.Persistence.Migrations;

/// <summary>One forward-only schema change. Applied migrations must never be edited.</summary>
public sealed record SchemaMigration(int Version, string Name, string Sql);
