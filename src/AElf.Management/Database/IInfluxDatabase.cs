using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InfluxData.Net.InfluxDb.Models.Responses;

namespace AElf.Management.Database
{
    public interface IInfluxDatabase
    {
        Task WriteAsync(string database, string measurement, Dictionary<string, object> fields,
            Dictionary<string, object> tags, DateTime timestamp);

        Task<List<Serie>> QueryAsync(string database, string query);

        Task<string> GetVersionAsync();

        Task CreateDatabaseAsync(string database);

        Task DropDatabaseAsync(string database);
    }
}