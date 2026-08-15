using AwesomeAssertions;
using Elarion.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.EntityFrameworkCore;

/// <summary>
/// Pins the comparer contract the framework exists to get right once: equality and hashing must agree, and both
/// halves must be order-dependent together. An order-independent equality paired with an order-dependent hash
/// (the usual hand-rolled slip) breaks <see cref="object.GetHashCode"/>'s contract and makes EF's change
/// detection depend on how the elements happened to be ordered.
/// </summary>
public sealed class ElarionValueComparersTests {
    [Fact]
    public void Sequence_EqualContents_AreEqual_AndHashAlike() {
        var comparer = ElarionValueComparers.Sequence<string>();
        string[] left = ["alpha", "beta"];
        string[] right = ["alpha", "beta"];

        comparer.Equals(left, right).Should().BeTrue();
        comparer.GetHashCode(left).Should().Be(comparer.GetHashCode(right));
    }

    [Fact]
    public void Sequence_Reordered_IsUnequal_AndHashesDifferently() {
        var comparer = ElarionValueComparers.Sequence<string>();
        string[] left = ["alpha", "beta"];
        string[] reordered = ["beta", "alpha"];

        comparer.Equals(left, reordered).Should().BeFalse();

        // The other half of the trap: an order-dependent equality must not be paired with an order-independent
        // hash, or two values EF considers different collide in every hash-keyed structure.
        comparer.GetHashCode(left).Should().NotBe(comparer.GetHashCode(reordered));
    }

    [Fact]
    public void Sequence_DifferentLengthOrElements_AreUnequal() {
        var comparer = ElarionValueComparers.Sequence<string>();

        comparer.Equals(["alpha"], ["alpha", "beta"]).Should().BeFalse();
        comparer.Equals(["alpha"], ["gamma"]).Should().BeFalse();
        comparer.Equals([], []).Should().BeTrue();
    }

    [Fact]
    public void Sequence_Snapshot_IsolatesFromInPlaceMutation() {
        var comparer = ElarionValueComparers.Sequence<string>();
        string[] original = ["alpha", "beta"];

        var snapshot = comparer.Snapshot(original);
        original[0] = "changed";

        // A snapshot that aliased the tracked array would make every in-place edit invisible to DetectChanges.
        snapshot.Should().Equal("alpha", "beta");
        comparer.Equals(snapshot, original).Should().BeFalse();
    }

    [Fact]
    public void SequenceList_HasTheSameSemantics() {
        var comparer = ElarionValueComparers.SequenceList<int>();
        List<int> left = [1, 2, 3];
        List<int> right = [1, 2, 3];
        List<int> reordered = [3, 2, 1];

        comparer.Equals(left, right).Should().BeTrue();
        comparer.GetHashCode(left).Should().Be(comparer.GetHashCode(right));
        comparer.Equals(left, reordered).Should().BeFalse();

        var snapshot = comparer.Snapshot(left);
        left.Add(4);
        snapshot.Should().Equal(1, 2, 3);
    }
}
