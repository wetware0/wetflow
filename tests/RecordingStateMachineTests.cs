using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class RecordingStateMachineTests
{
    // ── Hold mode ────────────────────────────────────────────────────────────

    [Fact]
    public void HandleKeyDown_WhenIdle_TransitionsToRecordingAndFiresEvent()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        var fired = false;
        sm.RecordingStarted += () => fired = true;

        sm.HandleKeyDown();

        Assert.True(fired);
        Assert.Equal(RecordingStateMachine.State.Recording, sm.CurrentState);
    }

    [Fact]
    public void HandleKeyUp_WhenRecordingHoldMode_TransitionsToTranscribing()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        sm.HandleKeyDown();
        var fired = false;
        sm.StoppedForTranscription += () => fired = true;

        sm.HandleKeyUp();

        Assert.True(fired);
        Assert.Equal(RecordingStateMachine.State.Transcribing, sm.CurrentState);
    }

    [Fact]
    public void HandleKeyDown_WhenRecordingHoldMode_IsNoOp()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        sm.HandleKeyDown();

        sm.HandleKeyDown();

        Assert.Equal(RecordingStateMachine.State.Recording, sm.CurrentState);
    }

    // ── Toggle mode ──────────────────────────────────────────────────────────

    [Fact]
    public void HandleKeyDown_SecondPressToggleMode_TransitionsToTranscribing()
    {
        var sm = new RecordingStateMachine(useToggleMode: true);
        sm.HandleKeyDown();
        var fired = false;
        sm.StoppedForTranscription += () => fired = true;

        sm.HandleKeyDown();

        Assert.True(fired);
        Assert.Equal(RecordingStateMachine.State.Transcribing, sm.CurrentState);
    }

    [Fact]
    public void HandleKeyUp_WhenRecordingToggleMode_IsNoOp()
    {
        var sm = new RecordingStateMachine(useToggleMode: true);
        sm.HandleKeyDown();

        sm.HandleKeyUp();

        Assert.Equal(RecordingStateMachine.State.Recording, sm.CurrentState);
    }

    // ── Overlay force-stop ───────────────────────────────────────────────────

    [Fact]
    public void HandleForceStop_WhenRecording_TransitionsToTranscribing()
    {
        var sm = new RecordingStateMachine(useToggleMode: true);
        sm.HandleKeyDown();
        var fired = false;
        sm.StoppedForTranscription += () => fired = true;

        sm.HandleForceStop();

        Assert.True(fired);
        Assert.Equal(RecordingStateMachine.State.Transcribing, sm.CurrentState);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public void HandleCancelled_WhenRecording_TransitionsToIdleAndFiresEvent()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        sm.HandleKeyDown();
        var fired = false;
        sm.RecordingCancelled += () => fired = true;

        sm.HandleCancelled();

        Assert.True(fired);
        Assert.Equal(RecordingStateMachine.State.Idle, sm.CurrentState);
    }

    [Fact]
    public void HandleCancelled_WhenTranscribing_FiresCancellationEventAndStaysTranscribing()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        sm.HandleKeyDown();
        sm.HandleKeyUp();
        var fired = false;
        sm.TranscriptionCancellationRequested += () => fired = true;

        sm.HandleCancelled();

        Assert.True(fired);
        Assert.Equal(RecordingStateMachine.State.Transcribing, sm.CurrentState);
    }

    // ── Transcription complete ────────────────────────────────────────────────

    [Fact]
    public void HandleTranscriptionComplete_WhenTranscribing_TransitionsToIdle()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        sm.HandleKeyDown();
        sm.HandleKeyUp();

        sm.HandleTranscriptionComplete();

        Assert.Equal(RecordingStateMachine.State.Idle, sm.CurrentState);
    }

    // ── Start failure ────────────────────────────────────────────────────────

    [Fact]
    public void HandleStartFailed_WhenRecording_TransitionsToIdle()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        sm.HandleKeyDown();

        sm.HandleStartFailed();

        Assert.Equal(RecordingStateMachine.State.Idle, sm.CurrentState);
    }

    // ── Guard: busy (Transcribing) ignores new start ─────────────────────────

    [Fact]
    public void HandleKeyDown_WhenTranscribing_IsNoOp()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        sm.HandleKeyDown();
        sm.HandleKeyUp();

        sm.HandleKeyDown();

        Assert.Equal(RecordingStateMachine.State.Transcribing, sm.CurrentState);
    }

    // ── Guard: Idle KeyUp is no-op ───────────────────────────────────────────

    [Fact]
    public void HandleKeyUp_WhenIdle_IsNoOp()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);

        sm.HandleKeyUp();

        Assert.Equal(RecordingStateMachine.State.Idle, sm.CurrentState);
    }

    // ── UseToggleMode can be changed after construction ───────────────────────

    [Fact]
    public void UseToggleMode_CanBeChangedAtRuntime()
    {
        var sm = new RecordingStateMachine(useToggleMode: false);
        sm.HandleKeyDown();

        sm.UseToggleMode = true;
        var fired = false;
        sm.StoppedForTranscription += () => fired = true;
        sm.HandleKeyDown();

        Assert.True(fired);
        Assert.Equal(RecordingStateMachine.State.Transcribing, sm.CurrentState);
    }
}
