using Agw.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

internal sealed class TestAgentDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestAgentDatabase()
    {
        // Production migrations do not create database foreign keys.
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        _connection.Open();
        Context = new AgwDbContext(new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(_connection).Options);
        Context.Database.EnsureCreated();
    }

    public AgwDbContext Context { get; }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
