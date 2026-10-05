// SQL stays at its feature call site so Dapper can generate parameter and row factories.
[module: Dapper.DapperAot]
[module: Dapper.SqlSyntax(Dapper.SqlSyntax.SQLite)]
