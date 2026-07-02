using AwesomeAssertions;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace Elarion.Tests.Blobs;

public sealed class PostgreSqlBlobStorageModelBuilderExtensionsTests {
    [Fact]
    public void UseElarionBlobStorage_ConfiguresBlobTables() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionBlobStorage();
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

    [Fact]
    public void UseElarionBlobStorage_ConfiguresLifecycleColumnsAndPartialIndex() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionBlobStorage();
        var model = modelBuilder.FinalizeModel();

        var storedBlob = model.FindEntityType(typeof(StoredBlob))!;
        storedBlob.FindProperty(nameof(StoredBlob.State))!.GetColumnName().Should().Be("state");
        storedBlob.FindProperty(nameof(StoredBlob.ExpiresAt))!.GetColumnName().Should().Be("expires_at");

        var pendingIndex = storedBlob.GetIndexes().Should().ContainSingle(index =>
            index.Properties.Count == 1 && index.Properties[0].Name == nameof(StoredBlob.ExpiresAt)).Subject;
        pendingIndex.IsUnique.Should().BeFalse();
        pendingIndex.GetFilter().Should().Be("state = 0 AND expires_at IS NOT NULL");
    }

    [Fact]
    public void UseElarionBlobStorage_SnakeCaseFalse_UsesPascalCaseNamesAndQuotedFilter() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionBlobStorage(snakeCase: false);
        var model = modelBuilder.FinalizeModel();

        var storedBlob = model.FindEntityType(typeof(StoredBlob))!;
        storedBlob.GetTableName().Should().Be("StoredBlobs");
        storedBlob.FindProperty(nameof(StoredBlob.ContentType))!.GetColumnName().Should().Be("ContentType");

        var pendingIndex = storedBlob.GetIndexes().Single(index =>
            index.Properties.Count == 1 && index.Properties[0].Name == nameof(StoredBlob.ExpiresAt));
        pendingIndex.GetFilter().Should().Be("\"State\" = 0 AND \"ExpiresAt\" IS NOT NULL");

        var blobContentRow = model.GetEntityTypes().Single(entityType => entityType.ClrType.Name == "BlobContentRow");
        blobContentRow.GetTableName().Should().Be("BlobContents");
    }

    [Fact]
    public void UseElarionBlobStorage_CustomTablesAndSchema_AreApplied() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionBlobStorage("my_blobs", "my_blob_contents", "files");
        var model = modelBuilder.FinalizeModel();

        var storedBlob = model.FindEntityType(typeof(StoredBlob))!;
        storedBlob.GetTableName().Should().Be("my_blobs");
        storedBlob.GetSchema().Should().Be("files");

        var blobContentRow = model.GetEntityTypes().Single(entityType => entityType.ClrType.Name == "BlobContentRow");
        blobContentRow.GetTableName().Should().Be("my_blob_contents");
        blobContentRow.GetSchema().Should().Be("files");
    }
}
