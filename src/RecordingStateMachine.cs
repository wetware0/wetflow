namespace WetFlow;

public sealed class RecordingStateMachine
{
    public enum State { Idle, Recording, Transcribing }

    public event Action? RecordingStarted;
    public event Action? StoppedForTranscription;
    public event Action? RecordingCancelled;
    public event Action? TranscriptionCancellationRequested;

    public State CurrentState { get; private set; } = State.Idle;
    public bool UseToggleMode { get; set; }

    public RecordingStateMachine(bool useToggleMode)
    {
        UseToggleMode = useToggleMode;
    }

    public void HandleKeyDown()
    {
        switch (CurrentState)
        {
            case State.Idle:
                CurrentState = State.Recording;
                RecordingStarted?.Invoke();
                break;
            case State.Recording when UseToggleMode:
                StopRecording();
                break;
        }
    }

    public void HandleKeyUp()
    {
        if (CurrentState == State.Recording && !UseToggleMode)
            StopRecording();
    }

    public void HandleCancelled()
    {
        switch (CurrentState)
        {
            case State.Recording:
                CurrentState = State.Idle;
                RecordingCancelled?.Invoke();
                break;
            case State.Transcribing:
                TranscriptionCancellationRequested?.Invoke();
                break;
        }
    }

    public void HandleStartFailed()
    {
        if (CurrentState == State.Recording)
            CurrentState = State.Idle;
    }

    public void HandleTranscriptionComplete()
    {
        if (CurrentState == State.Transcribing)
            CurrentState = State.Idle;
    }

    public void HandleForceStop()
    {
        if (CurrentState == State.Recording)
            StopRecording();
    }

    public void HandleOverlayToggle()
    {
        switch (CurrentState)
        {
            case State.Idle:
                HandleKeyDown();
                break;
            case State.Recording:
                HandleForceStop();
                break;
        }
    }

    private void StopRecording()
    {
        CurrentState = State.Transcribing;
        StoppedForTranscription?.Invoke();
    }
}
