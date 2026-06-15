namespace Bbs.Core;

/// <summary>
/// One forward target recorded for a message: the partner it was routed to and whether that leg
/// has been sent yet. Returned by <see cref="BbsStore.GetMessageForwards"/> to drive the webmail
/// Sent view's per-message status (Queued → partner / Forwarded). A message homed locally has no
/// <see cref="MessageForward"/> entries at all.
/// </summary>
/// <param name="PartnerCall">The partner BBS this leg forwards to.</param>
/// <param name="Forwarded">True once this leg has been sent (<c>forwarded_utc</c> stamped).</param>
public sealed record MessageForward(string PartnerCall, bool Forwarded);
