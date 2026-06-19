using AwesomeAssertions;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace Elarion.Tests.Blobs;

public sealed class PostgreSqlBlobStorageModelBuilderExtensionsTests {
    [Fact]
    public void UsePostgreSqlBlobStorage_ConfiguresBlobTables() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UsePostgreSqlBlobStorage();
        var model = modelBuilder.FinalizeModel();

        var storedBlob = model.FindEntityType(typeof(StoredBlob));
        storedBlob.Should().NotBeNull();
        storedBlob!.GetTableName().Should().Be("stored_blobs");
        storedBlob.FindPrimaryKey()!.Properties.Should().ContainSingle(property => property.Name == nameof(StoredBlob.Id));
        storedBlob.FindProperty(nameof(StoredBlob.ContentType))!.GetColumnName().Should().Be("content_type");
        var uniqueIndex = storedBlob.GetIndexes().Should().ContainSingle(index => index.IsUnique).Subject;
        uniqueIndex.Properties.Select(property => property.Name).Should().Equal(
            nameof(StoredBlob.Container),
            nameof(StoredBlob.Name));

        var blobContentRow = model.GetEntityTypes().Single(entityType => entityType.ClrType.Name == "BlobContentRow");
        blobContentRow.GetTableName().Should().Be("blob_contents");
        blobContentRow.FindProperty("BlobId")!.GetColumnName().Should().Be("blob_id");
        blobContentRow.FindProperty("Data")!.GetColumnName().Should().Be("data");
        blobContentRow.GetForeignKeys().Should().ContainSingle(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(StoredBlob) &&
            foreignKey.DeleteBehavior == DeleteBehavior.Cascade);
    }

}
