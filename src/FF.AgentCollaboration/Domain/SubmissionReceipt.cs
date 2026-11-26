namespace FF.AgentCollaboration.Domain;

internal enum SubmissionStatus { Unavailable, Awaiting, Confirmed }

// A source belongs to one native session. Begin captures the boundary before the envelope enters its terminal.
internal interface ISubmissionReceiptSource
{
    ISubmissionReceipt Begin(string messageId, string envelope);
}

internal interface ISubmissionReceipt
{
    SubmissionStatus Check(bool force = false);
}
