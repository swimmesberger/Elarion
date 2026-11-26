using AwesomeAssertions;
using Elarion.Spreadsheets;
using Xunit;

namespace Elarion.Tests.Spreadsheets;

public sealed class SpreadsheetRecordMappingTests {
    [Fact]
    public void PublicPropertiesWithAGetter_BecomeColumnsInDeclarationOrder() {
        var columns = ContactRow.SpreadsheetMapper.Columns;

        columns.Select(column => column.PropertyName).Should().Equal(
            "Name", "Email", "Phone", "CustomerSince", "Balance", "Visits", "Newsletter", "Kind", "Active",
            "NameLength");
        columns.Select(column => column.Index).Should().Equal(Enumerable.Range(0, columns.Count));
    }

    [Fact]
    public void ColumnNameAndDisplayName_SetTheHeaders_AndOrderMovesTheColumn() {
        ContactRow.Columns.Email.Header.Should().Be("E-mail");
        ContactRow.Columns.Email.Index.Should().Be(1);
        ContactRow.Columns.CustomerSince.Header.Should().Be("Customer since");
        ContactRow.Columns.Phone.Header.Should().Be("Phone");
    }

    [Fact]
    public void IgnoredProperty_HasNoColumn() {
        ContactRow.SpreadsheetMapper.Columns.Should().NotContain(column => column.PropertyName == "InternalNote");
    }

    [Fact]
    public void ComputedGetOnlyProperty_HasAColumn() {
        ContactRow.Columns.NameLength.Kind.Should().Be(SpreadsheetValueKind.Integer);
    }

    [Fact]
    public void ColumnConstants_DescribeTheColumn() {
        ContactRow.Columns.Email.Aliases.Should().Equal("Mail");
        ContactRow.Columns.Email.IsNullable.Should().BeTrue();
        ContactRow.Columns.Name.IsNullable.Should().BeFalse();
        ContactRow.Columns.Balance.Format.Should().Be("#,##0.00");
        ContactRow.Columns.Balance.Kind.Should().Be(SpreadsheetValueKind.Number);
        ContactRow.Columns.Newsletter.Kind.Should().Be(SpreadsheetValueKind.Boolean);
        ContactRow.Columns.CustomerSince.Kind.Should().Be(SpreadsheetValueKind.Date);
        ContactRow.Columns.Name.ToString().Should().Be("Name");
    }

    [Fact]
    public void EnumColumn_OffersDisplayNamesElseMemberNames() {
        ContactRow.Columns.Kind.Kind.Should().Be(SpreadsheetValueKind.Choice);
        ContactRow.Columns.Kind.Choices.Should().Equal("Private person", "Company", "Authority");
    }

    [Fact]
    public void LabelledBoolean_OffersItsLabels() {
        ContactRow.Columns.Active.Kind.Should().Be(SpreadsheetValueKind.Choice);
        ContactRow.Columns.Active.Choices.Should().Equal("Ja", "Nein");
    }

    [Fact]
    public void WorksheetName_IsTheDeclaredNameElseTheTypeName() {
        ContactRow.SpreadsheetMapper.WorksheetName.Should().Be("Contacts");
        ValueRow.SpreadsheetMapper.WorksheetName.Should().Be("ValueRow");
    }
}
