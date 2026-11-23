using MobMate.Core.Realtime;

internal static class OnsetFillerSelectionSmoke
{
    internal static void Run(Action<string, bool> check)
    {
        var draw = 0;
        var state = new OnsetFillerState(count => draw++ % count);
        FillerClip[] clips = Enumerable.Range(0, 5).Select(i => new FillerClip([(byte)i])).ToArray();
        var disabled = state.Configure(false, "en/voice-a");
        check("filler defaults OFF with no preparation", !state.Publish(disabled, clips) && state.BeginUtterance(1, true) is null);
        var current = state.Configure(true, "en/voice-a");
        check("unprepared utterance is consumed", state.BeginUtterance(2, true) is null);
        check("five prepared candidates installed", state.Publish(current, clips));
        check("preparation never replays an old onset", state.BeginUtterance(2, true) is null);
        var selected = state.BeginUtterance(3, true)!;
        check("prepared selection precedes playback", state.IsCurrent(selected));
        // A failed/cancelled selection does not change the last actually played.
        draw = 0;
        var retry = state.BeginUtterance(4, true)!;
        check("selection alone does not update last played", retry.Clip.Id == selected.Clip.Id);
        check("new utterance invalidates old selection", !state.IsCurrent(selected));
        check("actual start commits last played", state.RecordActualStart(retry));
        check("a selected attempt cannot start twice", !state.RecordActualStart(retry));
        for (ulong i = 5; i < 10; i++)
        {
            var next = state.BeginUtterance(i, true)!;
            check("random excludes the last actual start", next.Clip.Id != retry.Clip.Id);
            check("one attempt per utterance", state.BeginUtterance(i, true) is null);
            check("current attempt starts", state.RecordActualStart(next));
            retry = next;
        }
        check("single repeated candidate pool installed", state.Publish(current, [retry.Clip]));
        check("no candidate except previous means silence", state.BeginUtterance(30, true) is null);
        state.Publish(current, clips);
        check("permission rejection is consumed", state.BeginUtterance(31, false) is null && state.BeginUtterance(31, true) is null);
        var bodyRace = state.BeginUtterance(32, true)!;
        state.BodyReady();
        check("body ready prevents a delayed filler request", !state.IsCurrent(bodyRace));
        check("in-flight actual ACK survives body invalidation", state.RecordActualStart(bodyRace));
        check("actual ACK is recorded only once", !state.RecordActualStart(bodyRace));
        var afterAck = state.BeginUtterance(33, true)!;
        check("late ACK still excludes the actual sound", afterAck.Clip.Id != bodyRace.Clip.Id);
        var oldPreparation = current;
        current = state.Configure(true, "ja/voice-b");
        check("old voice/language cannot publish", !state.Publish(oldPreparation, clips));
        check("voice/language change requires preparation", state.BeginUtterance(34, true) is null);
        state.Publish(current, clips);
        var configRace = state.BeginUtterance(35, true)!;
        state.Configure(false, "ja/voice-b");
        check("active OFF invalidates old audio", !state.IsCurrent(configRace));
        current = state.Configure(true, "ja/voice-b");
        state.Publish(current, clips);
        check("OFF to ON does not replay a consumed onset", state.BeginUtterance(35, true) is null);
        var stopping = state.BeginUtterance(36, true)!;
        state.Invalidate();
        check("Stop invalidates playback and preparation", !state.IsCurrent(stopping) && !state.Publish(current, clips));
        state.BeginSession();
        current = state.Configure(true, "ja/voice-b");
        state.Publish(current, clips);
        var newSession = state.BeginUtterance(1, true)!;
        check("new native handle restarts its utterance sequence", newSession is not null);
        check("new session preserves last actual start", newSession is not null && newSession.Clip.Id != bodyRace.Clip.Id);
        var input = new byte[] { 1, 2, 3 };
        var immutable = new FillerClip(input);
        input[0] = 9;
        check("prepared bytes cannot be replaced by producer", immutable.Wav.Span[0] == 1);
        var lateAck = new OnsetFillerState(_ => 0);
        var latePreparation = lateAck.Configure(true, "en/voice");
        lateAck.Publish(latePreparation, clips);
        var earlier = lateAck.BeginUtterance(1, true)!;
        var accepted = lateAck.AcceptUtterance(2, true, out var consumed)!;
        lateAck.RecordActualStart(earlier); // Old output ACK arrives while the new onset is queued.
        check("late actual ACK is excluded after old output drains", consumed && lateAck.Choose(accepted)!.Clip.Id != earlier.Clip.Id);
        lateAck.AcceptUtterance(2, true, out consumed);
        check("duplicate onset does not invalidate an active attempt", !consumed && lateAck.Choose(accepted) != null);
    }
}
