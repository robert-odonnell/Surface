namespace Disruptor.Surface.Runtime;

/// <summary>
/// A single SurrealQL execution unit handed to <see cref="ISurrealExecutor"/>. Wraps
/// the rendered SQL plus an optional parameter dictionary; the executor decides how
/// to deliver each — parameters can be inlined into the SQL via
/// <see cref="SurrealFormatter"/> (today's HTTP/Embedded behaviour) or shipped to the
/// transport as native bindings (future evolution if SurrealDB's RPC binder learns to
/// preserve <c>Thing</c> types).
/// <para>
/// The library's compilers — <see cref="Query.QueryCompiler"/>,
/// <see cref="Query.EdgeQueryCompiler"/>, the commit emitter — render every literal
/// (record ids, string operands, range bounds) inline today, so consumers can
/// construct <c>SurrealCommand</c> with parameters left null. The slot exists for
/// callers writing their own SQL that wants to bind dynamically without manual
/// formatting.
/// </para>
/// </summary>
public sealed record SurrealCommand(
    string Sql,
    IReadOnlyDictionary<string, object?>? Parameters = null);
