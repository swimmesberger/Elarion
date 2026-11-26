using AwesomeAssertions;
using Elarion.Spreadsheets.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class SpreadsheetRecordGeneratorTests {
    private const string ContactSource =
        """
        using System.ComponentModel.DataAnnotations;
        using Elarion.Spreadsheets;

        namespace Sample.Contacts {
            public enum ContactKind {
                [Display(Name = "Private person")] Person = 0,
                Company = 1,
                Firm = 1
            }

            [SpreadsheetRecord("Contacts")]
            public sealed partial class ContactRow {
                public required string Name { get; init; }
                public string? Phone { get; init; }
                [SpreadsheetColumn("E-mail", Order = 1, Aliases = new[] { "Mail" })]
                public string? Email { get; init; }
                [Display(Name = "Customer since")]
                public System.DateOnly? CustomerSince { get; init; }
                [SpreadsheetColumn(Format = "#,##0.00", Width = 14)]
                public decimal Balance { get; init; }
                public ContactKind Kind { get; init; }
                public ContactKind? PreviousKind { get; init; }
                [SpreadsheetColumn(TrueLabel = "Ja", FalseLabel = "Nein")]
                public bool Active { get; init; }
                [SpreadsheetIgnore]
                public object? Tag { get; init; }
                public int NameLength => Name.Length;
            }
        }
        """;

    [Fact]
    public void SpreadsheetRecord_EmitsMapperWithColumnTableAndWriteCode() {
        var result = Generate(ContactSource);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Contacts_ContactRow.SpreadsheetRowMapper.g.cs");

        source.Should().Contain(
            "public sealed class ContactRowSpreadsheetRowMapper : global::Elarion.Spreadsheets.ISpreadsheetRowMapper<global::Sample.Contacts.ContactRow>");
        source.Should().Contain("public const string DefaultWorksheetName = \"Contacts\";");
        source.Should().Contain(
            "global::Elarion.Spreadsheets.Generated.SpreadsheetColumns.Create<global::Sample.Contacts.ContactRow>(0, \"Name\", \"Name\", global::Elarion.Spreadsheets.SpreadsheetValueKind.Text, false, null, 0d, global::System.Array.Empty<string>(), global::System.Array.Empty<string>()),");
        source.Should().Contain(
            "Create<global::Sample.Contacts.ContactRow>(1, \"E-mail\", \"Email\", global::Elarion.Spreadsheets.SpreadsheetValueKind.Text, true, null, 0d, new string[] { \"Mail\" }, global::System.Array.Empty<string>()),");
        source.Should().Contain(
            "Create<global::Sample.Contacts.ContactRow>(3, \"Customer since\", \"CustomerSince\", global::Elarion.Spreadsheets.SpreadsheetValueKind.Date, true,");
        source.Should().Contain(
            "Create<global::Sample.Contacts.ContactRow>(4, \"Balance\", \"Balance\", global::Elarion.Spreadsheets.SpreadsheetValueKind.Number, false, \"#,##0.00\", 14d,");
        source.Should().Contain(
            "(5, \"Kind\", \"Kind\", global::Elarion.Spreadsheets.SpreadsheetValueKind.Choice, false, null, 0d, global::System.Array.Empty<string>(), new string[] { \"Private person\", \"Company\" }),");
        source.Should().Contain(
            "(7, \"Active\", \"Active\", global::Elarion.Spreadsheets.SpreadsheetValueKind.Choice, false, null, 0d, global::System.Array.Empty<string>(), new string[] { \"Ja\", \"Nein\" }),");
        source.Should().Contain(
            "(8, \"NameLength\", \"NameLength\", global::Elarion.Spreadsheets.SpreadsheetValueKind.Integer, false,");
        source.Should().Contain("writer.WriteText(record.Name);");
        source.Should().Contain("writer.WriteDate(record.CustomerSince);");
        source.Should().Contain("writer.WriteDecimal(record.Balance);");
        source.Should().Contain("writer.WriteText(LabelOfKind(record.Kind));");
        source.Should().Contain(
            "writer.WriteText(record.PreviousKind is { } __PreviousKind ? LabelOfPreviousKind(__PreviousKind) : null);");
        source.Should().Contain("writer.WriteText(record.Active ? \"Ja\" : \"Nein\");");
        source.Should().Contain("writer.WriteInteger(record.NameLength);");
        source.Should().NotContain("Tag");
    }

    [Fact]
    public void EnumColumn_WritesDisplayNameElseMemberName() {
        var result = Generate(ContactSource);

        var source = GetGenerated(result, "Sample_Contacts_ContactRow.SpreadsheetRowMapper.g.cs");

        source.Should().Contain("global::Sample.Contacts.ContactKind.Person => \"Private person\",");
        source.Should().Contain("global::Sample.Contacts.ContactKind.Company => \"Company\",");
        // A member that repeats another's value has no case of its own: the switch would not compile.
        source.Should().NotContain("ContactKind.Firm =>");
        source.Should().Contain("_ => value.ToString()");
    }

    [Fact]
    public void SpreadsheetRecord_EmitsSelfMappingHalfWithColumnConstants() {
        var result = Generate(ContactSource);

        var source = GetGenerated(result, "Sample_Contacts_ContactRow.SpreadsheetRecord.g.cs");

        source.Should().Contain(
            "partial class ContactRow : global::Elarion.Spreadsheets.ISpreadsheetRecord<global::Sample.Contacts.ContactRow> {");
        source.Should().Contain(
            "public static global::Elarion.Spreadsheets.ISpreadsheetRowMapper<global::Sample.Contacts.ContactRow> SpreadsheetMapper => global::Sample.Contacts.ContactRowSpreadsheetRowMapper.Instance;");
        source.Should().Contain(
            "public static readonly global::Elarion.Spreadsheets.SpreadsheetColumn<global::Sample.Contacts.ContactRow> Email = global::Sample.Contacts.ContactRowSpreadsheetRowMapper.ColumnTable[1];");
    }

    [Fact]
    public void GeneratedSources_Compile() {
        var result = Generate(ContactSource);

        CompileErrors([ContactSource, .. result.GeneratedTrees.Select(tree => tree.GetText().ToString())])
            .Should().BeEmpty();
    }

    [Fact]
    public void RecordWithoutWorksheetName_UsesItsTypeName() {
        var result = Generate(
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial record BankAccountRow {
                    public required string Iban { get; init; }
                }
            }
            """);

        NoErrors(result);
        GetGenerated(result, "Sample_BankAccountRow.SpreadsheetRowMapper.g.cs")
            .Should().Contain("public const string DefaultWorksheetName = \"BankAccountRow\";");
        GetGenerated(result, "Sample_BankAccountRow.SpreadsheetRecord.g.cs")
            .Should().Contain("partial record BankAccountRow : global::Elarion.Spreadsheets.ISpreadsheetRecord<global::Sample.BankAccountRow> {");
    }

    [Fact]
    public void EverySupportedType_MapsToItsKind() {
        const string source =
            """
            namespace Sample {
                public enum Level { Low, High }

                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class AllTypes {
                    public string? Text { get; set; }
                    public bool Flag { get; set; }
                    public sbyte SByteValue { get; set; }
                    public byte ByteValue { get; set; }
                    public short ShortValue { get; set; }
                    public ushort UShortValue { get; set; }
                    public int IntValue { get; set; }
                    public uint UIntValue { get; set; }
                    public long LongValue { get; set; }
                    public ulong ULongValue { get; set; }
                    public float FloatValue { get; set; }
                    public double DoubleValue { get; set; }
                    public decimal DecimalValue { get; set; }
                    public System.DateTime DateTimeValue { get; set; }
                    public System.DateTimeOffset DateTimeOffsetValue { get; set; }
                    public System.DateOnly DateValue { get; set; }
                    public System.TimeOnly TimeValue { get; set; }
                    public System.Guid GuidValue { get; set; }
                    public Level LevelValue { get; set; }
                    public int? NullableInt { get; set; }
                    public System.DateTime? NullableDateTime { get; set; }
                    public bool? NullableFlag { get; set; }
                }
            }
            """;
        var result = Generate(source);

        NoErrors(result);
        var mapper = GetGenerated(result, "Sample_AllTypes.SpreadsheetRowMapper.g.cs");
        mapper.Should().Contain("writer.WriteBoolean(record.Flag);");
        mapper.Should().Contain("writer.WriteInteger(record.SByteValue);");
        mapper.Should().Contain("writer.WriteInteger(record.UIntValue);");
        mapper.Should().Contain("writer.WriteUnsignedInteger(record.ULongValue);");
        mapper.Should().Contain("writer.WriteSingle(record.FloatValue);");
        mapper.Should().Contain("writer.WriteDouble(record.DoubleValue);");
        mapper.Should().Contain("writer.WriteDateTime(record.DateTimeValue);");
        mapper.Should().Contain("writer.WriteDateTimeOffset(record.DateTimeOffsetValue);");
        mapper.Should().Contain("writer.WriteTime(record.TimeValue);");
        mapper.Should().Contain("writer.WriteGuid(record.GuidValue);");
        mapper.Should().Contain("writer.WriteInteger(record.NullableInt);");
        mapper.Should().Contain("writer.WriteBoolean(record.NullableFlag);");
        mapper.Should().Contain("SpreadsheetValueKind.DateTimeOffset, false,");
        mapper.Should().Contain("SpreadsheetValueKind.Guid, false,");
        mapper.Should().Contain("SpreadsheetValueKind.Time, false,");
        mapper.Should().Contain("\"NullableDateTime\", global::Elarion.Spreadsheets.SpreadsheetValueKind.DateTime, true,");
        CompileErrors(
                [source, .. result.GeneratedTrees.Select(tree => tree.GetText().ToString())])
            .Should().BeEmpty();
    }

    [Fact]
    public void BaseTypeProperties_ComeFirst() {
        var result = Generate(
            """
            namespace Sample {
                public abstract class RowBase { public string? Id { get; set; } }

                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Derived : RowBase { public string? Name { get; set; } }
            }
            """);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Derived.SpreadsheetRowMapper.g.cs");
        source.IndexOf("writer.WriteText(record.Id);", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("writer.WriteText(record.Name);", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedPropertyType_FailsTheBuildWithElsheet001() {
        var result = Generate(
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Row {
                    public string? Name { get; set; }
                    public System.TimeSpan Duration { get; set; }
                }
            }
            """);

        var diagnostic = result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSHEET001").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("Duration");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void UnsupportedPropertyMarkedIgnore_Builds() {
        var result = Generate(
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Row {
                    public string? Name { get; set; }
                    [Elarion.Spreadsheets.SpreadsheetIgnore]
                    public System.TimeSpan Duration { get; set; }
                }
            }
            """);

        NoErrors(result);
    }

    [Fact]
    public void RecordThatIsNotPartial_FailsTheBuildWithElsheet002() {
        var result = Generate(
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed class Row { public string? Name { get; set; } }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSHEET002")
            .Which.Severity.Should().Be(DiagnosticSeverity.Error);
        result.GeneratedTrees.Should().NotContain(tree => tree.FilePath.EndsWith("SpreadsheetRecord.g.cs"));
    }

    [Fact]
    public void HeadersOrAliasesEqualIgnoringCase_FailTheBuildWithElsheet003NamingBoth() {
        var result = Generate(
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Row {
                    [Elarion.Spreadsheets.SpreadsheetColumn("E-mail")]
                    public string? Email { get; set; }
                    [Elarion.Spreadsheets.SpreadsheetColumn(Aliases = new[] { "e-MAIL " })]
                    public string? Mail { get; set; }
                }
            }
            """);

        var diagnostic = result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSHEET003").Subject;
        diagnostic.GetMessage().Should().Contain("Email").And.Contain("Mail");
    }

    [Theory]
    [InlineData("Q1/Q2", "'/'")]
    [InlineData("", "empty")]
    [InlineData("A name that is much longer than thirty-one", "longer than 31")]
    [InlineData("'Quoted", "apostrophe")]
    [InlineData("Sheet[1]", "'['")]
    public void InvalidWorksheetName_FailsTheBuildWithElsheet004(string name, string reason) {
        var result = Generate(
            $$"""
              namespace Sample {
                  [Elarion.Spreadsheets.SpreadsheetRecord("{{name}}")]
                  public sealed partial class Row { public string? Name { get; set; } }
              }
              """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSHEET004")
            .Which.GetMessage().Should().Contain(reason);
    }

    [Theory]
    [InlineData("public string? Text { get; set; }", "TrueLabel = \"Ja\", FalseLabel = \"Nein\"")]
    [InlineData("public bool Flag { get; set; }", "TrueLabel = \"Ja\"")]
    [InlineData("public bool Flag { get; set; }", "FalseLabel = \"Nein\"")]
    [InlineData("public bool Flag { get; set; }", "TrueLabel = \"Ja\", FalseLabel = \"ja\"")]
    public void BooleanLabelsMisused_FailTheBuildWithElsheet005(string property, string labels) {
        var result = Generate(
            $$"""
              namespace Sample {
                  [Elarion.Spreadsheets.SpreadsheetRecord]
                  public sealed partial class Row {
                      [Elarion.Spreadsheets.SpreadsheetColumn({{labels}})]
                      {{property}}
                  }
              }
              """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSHEET005")
            .Which.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public void SpreadsheetRecordOnAStruct_FailsTheBuildWithElsheet006() {
        var result = Generate(
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public partial struct Row { public string? Name { get; set; } }

                [Elarion.Spreadsheets.SpreadsheetRecord]
                public partial record struct OtherRow(string Name);
            }
            """);

        result.Diagnostics.Where(d => d.Id == "ELSHEET006").Should().HaveCount(2)
            .And.OnlyContain(d => d.GetMessage().Contains("struct"));
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void NestedGenericOrAbstractRecord_FailsTheBuildWithElsheet006() {
        var result = Generate(
            """
            namespace Sample {
                public static partial class Outer {
                    [Elarion.Spreadsheets.SpreadsheetRecord]
                    public sealed partial class Nested { public string? Name { get; set; } }
                }

                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Generic<T> { public string? Name { get; set; } }

                [Elarion.Spreadsheets.SpreadsheetRecord]
                public abstract partial class Base { public string? Name { get; set; } }
            }
            """);

        result.Diagnostics.Where(d => d.Id == "ELSHEET006").Should().HaveCount(3);
    }

    [Theory]
    [InlineData("public string? Columns { get; set; }", "Columns")]
    [InlineData("public static int SpreadsheetMapper() => 0;", "SpreadsheetMapper")]
    public void MemberNamedLikeAGeneratedOne_FailsTheBuildWithElsheet007(string member, string name) {
        var result = Generate(
            $$"""
              namespace Sample {
                  [Elarion.Spreadsheets.SpreadsheetRecord]
                  public sealed partial class Row {
                      public string? Name { get; set; }
                      {{member}}
                  }
              }
              """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSHEET007")
            .Which.GetMessage().Should().Contain(name);
        result.GeneratedTrees.Should().NotContain(tree => tree.FilePath.EndsWith("SpreadsheetRecord.g.cs"));
    }

    [Theory]
    [InlineData("[Elarion.Spreadsheets.SpreadsheetChoice(Aliases = new[] { \"gas\" })] NaturalGas, Gas", "gas", "Gas")]
    [InlineData("[Elarion.Spreadsheets.SpreadsheetChoice(Aliases = new[] { \"gas\" })] NaturalGas, [System.ComponentModel.DataAnnotations.Display(Name = \"GAS\")] Other", "gas", "GAS")]
    [InlineData("[System.ComponentModel.DataAnnotations.Display(Name = \"Heating\")] Warmth, Heating", "Heating", "Heating")]
    public void EnumMembersWithAnEqualLabelNameOrAlias_FailTheBuildWithElsheet008NamingBoth(
        string members, string first, string second) {
        var result = Generate(
            $$"""
              namespace Sample {
                  public enum Source { {{members}} }

                  [Elarion.Spreadsheets.SpreadsheetRecord]
                  public sealed partial class Row {
                      public Source Source { get; set; }
                  }
              }
              """);

        var diagnostic = result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSHEET008").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain($"'{first}'").And.Contain($"'{second}'");
        diagnostic.GetMessage().Should().MatchRegex(@"member 'Sample\.Source\.\w+'.*member 'Sample\.Source\.\w+'");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void AliasEqualToItsOwnLabelOrName_OrEmpty_IsIgnored() {
        var result = Generate(
            """
            namespace Sample {
                public enum Source {
                    [System.ComponentModel.DataAnnotations.Display(Name = "Erdgas")]
                    [Elarion.Spreadsheets.SpreadsheetChoice(Aliases = new[] { "ERDGAS", "naturalgas", " ", "gas" })]
                    NaturalGas,
                    Heating
                }

                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Row {
                    public Source Source { get; set; }
                }
            }
            """);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Row.SpreadsheetRowMapper.g.cs");
        source.Should().Contain(
            "if (string.Equals(text, \"gas\", global::System.StringComparison.OrdinalIgnoreCase)) return global::Sample.Source.NaturalGas;");
        source.Should().NotContain("\"ERDGAS\"").And.NotContain("\" \"");
        source.Should().Contain("new string[] { \"Erdgas\", \"Heating\" }", "the dropdown offers only the labels");
        source.Should().Contain("global::Sample.Source.NaturalGas => \"Erdgas\",");
        source.Split("\"gas\"").Length.Should().Be(2, "an alias only reads");
    }

    [Fact]
    public void SpreadsheetRecord_EmitsTypedReadCodeAndAnObjectInitializer() {
        var result = Generate(ContactSource);

        var source = GetGenerated(result, "Sample_Contacts_ContactRow.SpreadsheetRowMapper.g.cs");

        source.Should().Contain(
            "public global::Sample.Contacts.ContactRow? ReadRow(global::Elarion.Spreadsheets.Generated.SpreadsheetCellReader reader) {");
        source.Should().Contain("var __Name = reader.ReadText(0);");
        source.Should().Contain("var __CustomerSince = reader.ReadDate(3);");
        source.Should().Contain("var __Balance = reader.ReadDecimal(4);");
        source.Should().Contain("var __Active = reader.ReadBoolean(7);");
        source.Should().Contain("if (reader.ReadChoice(5) is { } __KindText) {");
        source.Should().Contain("__Kind = ParseKind(__KindText);");
        source.Should().Contain("if (__Kind is null) reader.RejectChoice(5);");
        source.Should().Contain("if (reader.HasErrors) return null;");
        source.Should().Contain("return new global::Sample.Contacts.ContactRow() {");
        source.Should().Contain("Name = __Name!,");
        source.Should().Contain("Email = __Email,");
        source.Should().Contain("Balance = __Balance.GetValueOrDefault(),");
        source.Should().Contain("PreviousKind = __PreviousKind,");
        source.Should().NotContain("NameLength =", "a computed property is not read");
        source.Should().Contain("public bool HasValidation => false;");
    }

    [Fact]
    public void ComputedProperty_IsMarkedWriteOnlyInTheColumnTable() {
        var source = GetGenerated(Generate(ContactSource), "Sample_Contacts_ContactRow.SpreadsheetRowMapper.g.cs");

        source.Should().Contain(
            "(8, \"NameLength\", \"NameLength\", global::Elarion.Spreadsheets.SpreadsheetValueKind.Integer, false, null, 0d, global::System.Array.Empty<string>(), global::System.Array.Empty<string>(), isReadable: false),");
    }

    [Fact]
    public void EnumColumn_ReadsLabelThenMemberNameIgnoringCase() {
        var source = GetGenerated(Generate(ContactSource), "Sample_Contacts_ContactRow.SpreadsheetRowMapper.g.cs");

        source.Should().Contain("private static global::Sample.Contacts.ContactKind? ParseKind(string text) {");
        source.Should().Contain(
            "if (string.Equals(text, \"Private person\", global::System.StringComparison.OrdinalIgnoreCase)) return global::Sample.Contacts.ContactKind.Person;");
        source.Should().Contain(
            "if (string.Equals(text, \"Person\", global::System.StringComparison.OrdinalIgnoreCase)) return global::Sample.Contacts.ContactKind.Person;");
        // "Company" is both a label and a name: one case in each of ParseKind and ParsePreviousKind.
        source.Split("\"Company\", global::System.StringComparison").Length.Should().Be(3);
    }

    [Fact]
    public void IntegralColumn_ReadsWithinItsTypesRange() {
        var result = Generate(
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Counts {
                    public byte Small { get; set; }
                    public int? Count { get; set; }
                    public ulong Big { get; set; }
                }
            }
            """);

        var source = GetGenerated(result, "Sample_Counts.SpreadsheetRowMapper.g.cs");
        source.Should().Contain("var __Small = (byte?)reader.ReadInteger(0, byte.MinValue, byte.MaxValue);");
        source.Should().Contain("var __Count = (int?)reader.ReadInteger(1, int.MinValue, int.MaxValue);");
        source.Should().Contain("var __Big = reader.ReadUnsignedInteger(2);");
    }

    [Fact]
    public void PositionalRecord_IsBuiltThroughItsConstructor() {
        const string source =
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial record AccountRow(string Iban, decimal Balance) {
                    public string? Holder { get; init; }
                }
            }
            """;
        var result = Generate(source);

        NoErrors(result);
        GetGenerated(result, "Sample_AccountRow.SpreadsheetRowMapper.g.cs").Should()
            .Contain("return new global::Sample.AccountRow(__Iban!, __Balance.GetValueOrDefault()) {")
            .And.Contain("Holder = __Holder,");
        CompileErrors([source, .. result.GeneratedTrees.Select(tree => tree.GetText().ToString())]).Should().BeEmpty();
    }

    [Fact]
    public void RequiredMemberThatIsNoReadColumn_MakesReadingThrow() {
        const string source =
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Secret {
                    public string? Name { get; init; }
                    [Elarion.Spreadsheets.SpreadsheetIgnore]
                    public required string Token { get; init; }
                }
            }
            """;
        var result = Generate(source);

        NoErrors(result);
        GetGenerated(result, "Sample_Secret.SpreadsheetRowMapper.g.cs").Should().Contain(
            "throw new global::System.NotSupportedException(\"The spreadsheet record 'Sample.Secret' cannot be read: its required member 'Token' is not a column the reader sets.\");");
        CompileErrors([source, .. result.GeneratedTrees.Select(tree => tree.GetText().ToString())]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("[System.ComponentModel.DataAnnotations.Required] public string? Name { get; init; }", "true")]
    [InlineData("[System.ComponentModel.DataAnnotations.Display(Name = \"N\")] public string? Name { get; init; }", "false")]
    [InlineData("[Elarion.Spreadsheets.SpreadsheetIgnore][System.ComponentModel.DataAnnotations.Range(1, 2)] public int Hidden { get; init; }", "true")]
    public void RecordWithDataAnnotations_SaysItCarriesValidation(string member, string expected) {
        var result = Generate($$"""
                                namespace Sample {
                                    [Elarion.Spreadsheets.SpreadsheetRecord]
                                    public sealed partial class Row {
                                        public string? Other { get; init; }
                                        {{member}}
                                    }
                                }
                                """);

        GetGenerated(result, "Sample_Row.SpreadsheetRowMapper.g.cs").Should().Contain($"public bool HasValidation => {expected};");
    }

    [Fact]
    public void ValidatableObject_CarriesValidation() {
        var result = Generate(
            """
            namespace Sample {
                [Elarion.Spreadsheets.SpreadsheetRecord]
                public sealed partial class Row : System.ComponentModel.DataAnnotations.IValidatableObject {
                    public string? Name { get; init; }
                    public System.Collections.Generic.IEnumerable<System.ComponentModel.DataAnnotations.ValidationResult> Validate(
                        System.ComponentModel.DataAnnotations.ValidationContext context) => [];
                }
            }
            """);

        GetGenerated(result, "Sample_Row.SpreadsheetRowMapper.g.cs").Should().Contain("public bool HasValidation => true;");
    }

    [Fact]
    public void IrrelevantEditReusesTrackedOutputs() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new SpreadsheetRecordGenerator(), ContactSource, "SpreadsheetRecords");
    }

    [Fact]
    public void UnrelatedFileEditDoesNotRerunDiscovery() {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new SpreadsheetRecordGenerator(), ContactSource, "SpreadsheetRecords");
    }

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static GeneratorDriverRunResult Generate(string source) {
        var compilation = CSharpCompilation.Create(
            "SpreadsheetRecordGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, ParseOptions)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SpreadsheetRecordGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static void NoErrors(GeneratorDriverRunResult result) {
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static IReadOnlyList<Diagnostic> CompileErrors(string[] sources) {
        var compilation = CSharpCompilation.Create(
            "SpreadsheetRecordGeneratedCompile",
            [.. sources.Select(source => CSharpSyntaxTree.ParseText(source, ParseOptions))],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return [
            .. trustedPlatformAssemblies!
                .Split(Path.PathSeparator)
                .Select(path => SharedMetadataReferences.FromFile(path)),
            SharedMetadataReferences.FromFile(typeof(Elarion.Spreadsheets.SpreadsheetRecordAttribute).Assembly.Location)
        ];
    }
}
