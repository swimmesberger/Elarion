using Microsoft.Extensions.Primitives;

namespace Elarion.Abstractions.Substitution;

/// <summary>
/// An <see cref="IVariableSource"/> that can signal when its values may have changed, so consumers can react
/// to runtime changes (for example the scheduler re-resolving a job's <c>${...}</c> schedule live) rather
/// than only re-reading on their next natural cycle. Sources that cannot observe change (a static dictionary)
/// simply do not implement this.
/// </summary>
public interface IObservableVariableSource : IVariableSource {
    /// <summary>
    /// Returns a change token that fires when a value may have changed. The token is one-shot — re-watch (or
    /// use <see cref="ChangeToken.OnChange(System.Func{IChangeToken}, System.Action)"/>) to keep observing.
    /// </summary>
    IChangeToken Watch();
}
