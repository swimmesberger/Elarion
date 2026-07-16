namespace Elarion;

// The invocation lease is the only component that can observe an accepted stream which is explicitly released
// before anyone asks it for an enumerator. Stream observability implements this optional internal callback.
internal interface IStreamInvocationObservation {
    void Abandon();
}
