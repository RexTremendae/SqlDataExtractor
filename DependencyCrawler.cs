namespace SqlDataExtractor
{
    using ForeignKeyMap = Dictionary<(string table, string column), (string table, string column)>;
    using TableMetadataMap = Dictionary<string, DatabaseTableMetadata>;

    public class DependencyCrawler
    {
        private readonly TableMetadataMap _tables;
        private readonly ForeignKeyMap _foreignKeys;

        public DependencyCrawler(ForeignKeyMap foreignKeys, TableMetadataMap tables)
        {
            _tables = tables;
            _foreignKeys = foreignKeys;
        }

        public async Task<IEnumerable<InsertQueryBuildingBlocks>> GetInsertQueriesBuildingBlocksAsync
            (IDbConnection connection, string startingTable, string keyColumn, params object[] keyColumnValues)
        {
            var tableQueue = new List<(DatabaseTableMetadata table, string keyColumn, object[] keyColumnValues)>();
            tableQueue.Add((_tables[startingTable], keyColumn, keyColumnValues));

            var insertQueryTablesVisited = new HashSet<string>();
            var insertQueryTableData = new List<InsertQueryBuildingBlocks>();

            while (tableQueue.Any())
            {
                var dequeued = tableQueue.First();
                tableQueue.RemoveAt(0);

                if (insertQueryTablesVisited.Contains(dequeued.table.Name))
                {
                    continue;
                }
                insertQueryTablesVisited.Add(dequeued.table.Name);

                var selectQuery = dequeued.table.CreateSelectQuery(
                    keyColumn: dequeued.keyColumn,
                    keyColumnFilter: dequeued.keyColumnValues);

                var data = new List<Dictionary<string, object?>>();

                await using var reader = await connection.ExecuteReaderAsync(selectQuery);

                while (await reader.ReadAsync())
                {
                    var dataRow = new Dictionary<string, object?>();
                    data.Add(dataRow);

                    foreach (var col in dequeued.table.Columns)
                    {
                        dataRow.Add(col.Name, reader.GetValue(col));
                    }
                }

                insertQueryTableData.Add(new InsertQueryBuildingBlocks(dequeued.table, data));

                // Add child dependencies
                foreach (var (from, to) in _foreignKeys.Where(fk => fk.Key.table == dequeued.table.Name))
                {
                    keyColumnValues = data.Select(d => d[from.column]).Cast<object>().ToArray();
                    tableQueue.Add((_tables[to.table], to.column, keyColumnValues));
                }

                // Add parent dependencies
                foreach (var (from, to) in _foreignKeys.Where(fk => fk.Value.table == dequeued.table.Name))
                {
                    keyColumnValues = data.Select(d => d[to.column]).Cast<object>().ToArray();
                    tableQueue.Add((_tables[from.table], from.column, keyColumnValues));
                }
            }

            return OrderByDependencies(insertQueryTableData);
        }

        private IEnumerable<InsertQueryBuildingBlocks> OrderByDependencies(List<InsertQueryBuildingBlocks> insertQueryBuildingBlocks)
        {
            var sorted = new List<InsertQueryBuildingBlocks>();

            foreach (var insertQueryBlocks in insertQueryBuildingBlocks)
            {
                var childDependencies = _foreignKeys
                    .Where(fk => fk.Key.table == insertQueryBlocks.tableMetadata.Name)
                    .Select(fk => fk.Value.table)
                    .ToHashSet();

                var insertIdx = 0;
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (childDependencies.Contains(sorted[i].tableMetadata.Name))
                    {
                        insertIdx = i+1;
                    }
                }

                sorted.Insert(insertIdx, insertQueryBlocks);
            }

            return sorted;
        }
    }
}