namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// Keeps <c>TransactionDecorator</c> off a command: the handler runs without a framework-owned unit of work, so each
/// <c>SaveChanges</c> commits on its own and nothing holds a connection or a transaction for the whole call.
/// </summary>
/// <remarks>
/// <para>
/// For a command whose work is mostly outside the database — sending a test mail, calling a slow external service —
/// where a transaction around the call would pin a pooled connection for its whole duration. The request stays an
/// <see cref="ICommand"/>, so everything else the request kind decides (the schema's read/write marking, view-as,
/// caching rules) is unchanged.
/// </para>
/// <para>
/// The handler owns atomicity: open a transaction itself (<c>db.Database.BeginTransactionAsync</c>, or
/// <c>IUnitOfWork.BeginAsync</c>) around the writes that must commit together. <c>[Idempotent]</c> commands and the
/// inbox of integration-event consumers own their transaction regardless; the attribute does not change them.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Handler("settings.smtp.test"), NoTransaction]
/// public sealed class TestSmtp(ISmtpClient smtp) : IHandler&lt;TestSmtp.Command, Result&lt;Unit&gt;&gt; { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class NoTransactionAttribute : Attribute;
