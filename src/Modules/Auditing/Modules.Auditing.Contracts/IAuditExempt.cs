namespace Boilerplate.Modules.Auditing.Contracts;

/// <summary>
/// Marker interface — an entity whose changes must never reach the audit trail, masked or not.
/// Implement this for a row whose very existence or shape is not audit's business (a distinct concern
/// from a sensitive-looking property, which <c>SensitiveFieldNames</c> masks while still recording
/// that the entity changed). The entity-change interceptor skips a matching entry entirely: no
/// EntityChange event is published, and no PropertyChange for it appears in AuditRecords.
/// </summary>
public interface IAuditExempt { }
