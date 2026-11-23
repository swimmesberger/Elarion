using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.JsonRpc;
using Xunit;

namespace Elarion.Tests.Abstractions;

public sealed class DecisionTests {
    private enum DuplicateChoice {
        Import,
        Skip
    }

    private sealed record Command(params DecisionAnswer[] Answers) : IDecidingRequest {
        public IReadOnlyList<DecisionAnswer>? Decisions => Answers.Length == 0 ? null : Answers;
    }

    [Fact]
    public void DecisionRequired_IsItsOwnKindWithTheDecisionsCode() {
        var error = AppError.DecisionRequired("banking.possible_duplicate", "Import anyway?", new { Row = 3 });

        error.Kind.Should().Be(ErrorKind.DecisionRequired);
        error.Code.Should().Be("banking.possible_duplicate");
        ErrorCodes.ForKind(ErrorKind.DecisionRequired).Should().Be("decision_required");
    }

    [Fact]
    public void DecisionRequired_MapsToItsOwnJsonRpcCode() {
        // A generic client recognizes every decision by the kind, without knowing its code.
        AppErrorMapper.MapToCode(ErrorKind.DecisionRequired).Should().Be(-32006);
        AppErrorMapper.ToRpcError(AppError.DecisionRequired("a.b", "?")).Data!.Code.Should().Be("a.b");
    }

    [Fact]
    public void IsConfirmed_ReadsAWholeRequestConfirmation() {
        new Command().IsConfirmed("banking.duplicate").Should().BeFalse();
        new Command(new DecisionAnswer { Code = "banking.duplicate" }).IsConfirmed("banking.duplicate").Should().BeTrue();
        new Command(new DecisionAnswer { Code = "banking.other" }).IsConfirmed("banking.duplicate").Should().BeFalse();
        // A per-item answer does not confirm the whole request.
        new Command(new DecisionAnswer { Code = "banking.duplicate", Item = "3" })
            .IsConfirmed("banking.duplicate").Should().BeFalse();
    }

    [Fact]
    public void Decision_ParsesTheCamelCasedOptionAndIgnoresAnUndeclaredOne() {
        new Command(new DecisionAnswer { Code = "c", Option = "skip" }).Decision<DuplicateChoice>("c")
            .Should().Be(DuplicateChoice.Skip);
        new Command(new DecisionAnswer { Code = "c", Option = "maybe" }).Decision<DuplicateChoice>("c")
            .Should().BeNull();
        // Enum.TryParse would accept a number; an option is a name.
        new Command(new DecisionAnswer { Code = "c", Option = "1" }).Decision<DuplicateChoice>("c").Should().BeNull();
        new Command().Decision<DuplicateChoice>("c").Should().BeNull();
    }

    [Fact]
    public void PerItemAnswersAreReadPerItem_AndTheLastAnswerWins() {
        var command = new Command(
            new DecisionAnswer { Code = "c", Item = "line-1", Option = "import" },
            new DecisionAnswer { Code = "c", Item = "line-2", Option = "skip" },
            new DecisionAnswer { Code = "c", Item = "line-1", Option = "skip" },
            new DecisionAnswer { Code = "c", Item = "line-4" });

        command.Decision<DuplicateChoice>("c", "line-1").Should().Be(DuplicateChoice.Skip);
        command.Decision<DuplicateChoice>("c", "line-2").Should().Be(DuplicateChoice.Skip);
        command.Decision<DuplicateChoice>("c", "line-3").Should().BeNull();
        command.IsConfirmed("c", "line-4").Should().BeTrue();
        command.IsConfirmed("c", "line-1").Should().BeFalse();
    }

    [Fact]
    public void Unanswered_ListsTheItemsTheCallerDidNotAnswer_InOrder() {
        var command = new Command(
            new DecisionAnswer { Code = "c", Item = "line-2" },
            new DecisionAnswer { Code = "other", Item = "line-3" });

        command.Unanswered("c", ["line-1", "line-2", "line-3"]).Should().Equal("line-1", "line-3");
        new Command().Unanswered("c", ["line-1"]).Should().Equal("line-1");
        command.Unanswered("c", ["line-2"]).Should().BeEmpty();
    }

    [Fact]
    public void Unanswered_OfAConfirmation_CountsOnlyAConfirmingAnswer() {
        var command = new Command(
            DecisionAnswer.Confirming("c", "line-1"),
            new DecisionAnswer { Code = "c", Item = "line-2", Option = "skip" },
            new DecisionAnswer { Code = "c", Item = "line-3", Option = "CONFIRM" });

        // An answer with another option confirms nothing, so the item is asked about again.
        command.Unanswered("c", ["line-1", "line-2", "line-3"]).Should().Equal("line-2");
    }

    [Fact]
    public void Unanswered_OfAChoice_CountsOnlyAnOptionTheEnumDeclares() {
        var command = new Command(
            DecisionAnswer.Choosing("c", "line-1", DuplicateChoice.Import),
            new DecisionAnswer { Code = "c", Item = "line-2", Option = "maybe" },
            new DecisionAnswer { Code = "c", Item = "line-3", Option = "1" },
            DecisionAnswer.Confirming("c", "line-4"),
            new DecisionAnswer { Code = "c", Item = "line-5", Option = "SKIP" });

        command.Unanswered<DuplicateChoice>("c", ["line-1", "line-2", "line-3", "line-4", "line-5", "line-6"])
            .Should().Equal("line-2", "line-3", "line-4", "line-6");
    }

    [Fact]
    public void Unanswered_OfAChoice_ReadsTheLastAnswerForAnItem() {
        var command = new Command(
            new DecisionAnswer { Code = "c", Item = "line-1", Option = "import" },
            new DecisionAnswer { Code = "c", Item = "line-1", Option = "maybe" },
            new DecisionAnswer { Code = "c", Item = "line-2", Option = "maybe" },
            new DecisionAnswer { Code = "c", Item = "line-2", Option = "skip" });

        command.Unanswered<DuplicateChoice>("c", ["line-1", "line-2"]).Should().Equal("line-1");
    }

    [Fact]
    public void Unanswered_OfAChoice_LeavesNoItemWithoutADecision() {
        // A handler refuses while Unanswered is not empty, then acts on each item's Decision: an item must never pass
        // the first check and then read as no decision in the second.
        var command = new Command(
            DecisionAnswer.Choosing("c", "line-1", DuplicateChoice.Import),
            DecisionAnswer.Choosing("c", "line-2", DuplicateChoice.Skip),
            new DecisionAnswer { Code = "c", Item = "line-3", Option = "maybe" },
            new DecisionAnswer { Code = "c", Option = "import" },
            new DecisionAnswer { Code = "other", Item = "line-4", Option = "import" });
        string[] items = ["line-1", "line-2", "line-3", "line-4"];

        var unanswered = command.Unanswered<DuplicateChoice>("c", items);

        foreach (var item in items)
            unanswered.Contains(item).Should().Be(command.Decision<DuplicateChoice>("c", item) is null, item);
    }

    private enum ExportedChange {
        OnlyHere,
        AsCorrection
    }

    [Fact]
    public void Confirming_BuildsTheConfirmationAnswer_ForTheRequestOrAnItem() {
        DecisionAnswer.Confirming("c").Should().Be(new DecisionAnswer { Code = "c" });
        DecisionAnswer.Confirming("c", "line-1").Should().Be(new DecisionAnswer { Code = "c", Item = "line-1" });

        var command = new Command(DecisionAnswer.Confirming("c"), DecisionAnswer.Confirming("d", "line-1"));
        command.IsConfirmed("c").Should().BeTrue();
        command.IsConfirmed("d", "line-1").Should().BeTrue();
        command.IsConfirmed("d").Should().BeFalse();
    }

    [Fact]
    public void Choosing_SpellsTheOptionAsTheSchemaExportsIt_AndDecisionReadsItBack() {
        var whole = DecisionAnswer.Choosing("c", ExportedChange.OnlyHere);
        var perItem = DecisionAnswer.Choosing("d", "line-2", DuplicateChoice.Skip);

        whole.Should().Be(new DecisionAnswer { Code = "c", Option = "onlyHere" });
        perItem.Should().Be(new DecisionAnswer { Code = "d", Item = "line-2", Option = "skip" });
        var command = new Command(whole, perItem);
        command.Decision<ExportedChange>("c").Should().Be(ExportedChange.OnlyHere);
        command.Decision<DuplicateChoice>("d", "line-2").Should().Be(DuplicateChoice.Skip);
    }

    [Fact]
    public void Choosing_RejectsAValueTheEnumDoesNotDeclare() {
        var act = () => DecisionAnswer.Choosing("c", (DuplicateChoice)7);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("option");
    }

    [Fact]
    public void AnswersRoundTripWithTheOptionDefaultingToConfirm() {
        var info = DecisionJsonContext.Default.DecisionAnswer;

        var answer = JsonSerializer.Deserialize("""{"code":"c","item":"line-1"}""", info)!;

        answer.Option.Should().Be(DecisionAnswer.Confirm);
        JsonNode.Parse(JsonSerializer.Serialize(answer, info))!["option"]!.GetValue<string>().Should().Be("confirm");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DecisionAnswer))]
internal sealed partial class DecisionJsonContext : JsonSerializerContext;
