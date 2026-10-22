using Solo.Api.Testing;
using Xunit;
namespace Solo.DesignDocs.Tests;
[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
