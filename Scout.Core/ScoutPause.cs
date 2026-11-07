namespace Scout.Core;
public sealed class ScoutPausedException():OperationCanceledException("Scout paused at a saved checkpoint.");
public sealed class ScoutPause {
 private int requested;
 public bool IsRequested=>Volatile.Read(ref requested)!=0;
 public void Request()=>Interlocked.Exchange(ref requested,1);
 public void Checkpoint(){if(IsRequested)throw new ScoutPausedException();}
}
