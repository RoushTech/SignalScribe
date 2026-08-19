namespace SignalScribe.Analysis;

/// <summary>
/// Whether an existing session can absorb a transmission.
///
/// The obvious half is the gap: a transmission arriving within the join gap of a session's end
/// continues it. The half that is easy to omit — and was — is that the session must not *start
/// after* the transmission. Live traffic arrives in time order, so the omission is invisible: the
/// most recent session is always the right candidate. It only bites when transmissions are
/// processed out of order, which is exactly what re-sessionizing does, and then the arithmetic
/// works against you — the gap is negative, so a naive check passes and an hours-old transmission
/// is swallowed by a session that started long after it. Measured, back-filling one afternoon put
/// 980 transmissions into a single 28-minute session.
/// </summary>
public static class SessionContinuity
{
    public static bool CanAbsorb(DateTime sessionStartUtc, DateTime sessionEndUtc, DateTime transmissionStartUtc, TimeSpan joinGap) =>
        sessionStartUtc <= transmissionStartUtc
        && transmissionStartUtc - sessionEndUtc <= joinGap;
}
