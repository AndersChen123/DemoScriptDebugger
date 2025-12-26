using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Demo {
	public class ScriptFrame {
		public string MethodName { get; init; } = "";
		public int CheckpointId { get; init; }
		public IReadOnlyList<(string Name, object? Value)> Locals { get; init; } = Array.Empty<(string, object?)>();
	}

	public class ScriptDebugger {
		public enum StepActionKind { None, StepInto, StepOver, StepOut }

		public event Action<int, ScriptFrame, int>? Paused;

		readonly HashSet<int> breakpoints = new();
		readonly Dictionary<string, List<int>> methodCheckpointMap;
		readonly ConcurrentDictionary<int, ThreadState> threadStates = new();
		int nextPauseId = 1;

		public ScriptDebugger(Dictionary<string, List<int>> methodCheckpointMap) {
			this.methodCheckpointMap = methodCheckpointMap ?? new Dictionary<string, List<int>>();
		}

		public void AddBreakpoint(int checkpointId) { lock (breakpoints) { breakpoints.Add(checkpointId); } }
		public void RemoveBreakpoint(int checkpointId) { lock (breakpoints) { breakpoints.Remove(checkpointId); } }
		bool HasBreakpoint(int checkpointId) { lock (breakpoints) { return breakpoints.Contains(checkpointId); } }

		// Methods called by injected code
		public void PushFrame(string methodName, Func<(string Name, object? Value)[]>? localsProvider) {
			var tid = Thread.CurrentThread.ManagedThreadId;
			var ts = threadStates.GetOrAdd(tid, id => new ThreadState(tid));
			lock (ts.Lock) {
				var locals = localsProvider?.Invoke() ?? Array.Empty<(string, object?)>();
				var info = new FrameInfo { MethodName = methodName, Locals = locals.ToArray(), LastCheckpointId = -1 };
				ts.FrameStack.Push(info);
			}
		}

		public void PopFrame() {
			var tid = Thread.CurrentThread.ManagedThreadId;
			if (!threadStates.TryGetValue(tid, out var ts)) return;
			lock (ts.Lock) {
				if (ts.FrameStack.Count > 0)
					ts.FrameStack.Pop();
				if (ts.PauseOnPopToDepth is int target && ts.FrameStack.Count <= target) {
					ts.PauseOnPopToDepth = null;
					ts.PauseNextCheckpoint = true;
				}
			}
		}

		public void Checkpoint(int checkpointId, string methodName, Func<(string Name, object? Value)[]>? localsProvider) {
			var tid = Thread.CurrentThread.ManagedThreadId;
			var ts = threadStates.GetOrAdd(tid, id => new ThreadState(id));

			FrameInfo? currentFrame = null;
			lock (ts.Lock) {
				if (ts.FrameStack.Count > 0)
					currentFrame = ts.FrameStack.Peek();
			}

			var frameData = (method: methodName, cp: checkpointId, 
        		locals: (IReadOnlyList<(string Name, object? Value)>)Array.Empty<(string, object?)>());	
			var localsArr = localsProvider?.Invoke();
			if (localsArr != null)
				frameData = (methodName, checkpointId, localsArr);
			else if (currentFrame != null)
				frameData = (currentFrame.MethodName, checkpointId, currentFrame.Locals);
			else
				frameData = (methodName, checkpointId, Array.Empty<(string, object?)>());

			lock (ts.Lock) {
				if (ts.FrameStack.Count > 0) {
					var top = ts.FrameStack.Pop();
					top.LastCheckpointId = checkpointId;
					ts.FrameStack.Push(top);
				}
			}

			bool pauseBecauseBreakpoint = HasBreakpoint(checkpointId);
			bool pauseBecauseStep = false;
			lock (ts.Lock) {
				if (ts.PauseNextCheckpoint) {
					pauseBecauseStep = true;
					ts.PauseNextCheckpoint = false;
					ts.ClearStepState();
				}
				else {
					switch (ts.StepAction) {
						case StepActionKind.StepInto:
							pauseBecauseStep = true;
							ts.ClearStepState();
							break;

						case StepActionKind.StepOver:
							if (ts.RunUntilCheckpointId.HasValue) {
								if (ts.RunUntilCheckpointId.Value == checkpointId) {
									pauseBecauseStep = true;
									ts.RunUntilCheckpointId = null;
									ts.StepAction = StepActionKind.None;
								}
							}
							break;

						case StepActionKind.StepOut:
							// wait for pop
							break;

						default:
							break;
					}
				}
			}

			if (!pauseBecauseBreakpoint && !pauseBecauseStep)
				return;

			var pauseId = Interlocked.Increment(ref nextPauseId);
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

			lock (ts.Lock) {
				ts.CurrentTcs = tcs;
				ts.CurrentPauseId = pauseId;
				ts.LastPausedFrame = new ScriptFrame { MethodName = frameData.method, CheckpointId = frameData.cp, Locals = frameData.locals.ToArray() };
			}

			Paused?.Invoke(pauseId, ts.LastPausedFrame!, tid);

			try {
				tcs.Task.Wait();
			}
			finally {
				lock (ts.Lock) {
					ts.CurrentTcs = null;
					ts.CurrentPauseId = 0;
				}
			}
		}

		// UI calls
		public void Continue(int pauseId) {
			if (!TryGetThreadStateByPauseId(pauseId, out var ts)) return;
			lock (ts.Lock) {
				ts.ClearStepState();
				ts.CurrentTcs?.TrySetResult(true);
			}
		}

		public void StepInto(int pauseId) {
			if (!TryGetThreadStateByPauseId(pauseId, out var ts)) return;
			lock (ts.Lock) {
				ts.StepAction = StepActionKind.StepInto;
				ts.CurrentTcs?.TrySetResult(true);
			}
		}

		public void StepOver(int pauseId) {
			if (!TryGetThreadStateByPauseId(pauseId, out var ts)) return;
			lock (ts.Lock) {
				if (ts.FrameStack.Count == 0) {
					ts.StepAction = StepActionKind.StepInto;
					ts.CurrentTcs?.TrySetResult(true);
					return;
				}

				var top = ts.FrameStack.Peek();
				var methodName = top.MethodName;
				var currentCheckpoint = top.LastCheckpointId;

				int? nextCp = null;
				if (methodCheckpointMap.TryGetValue(methodName, out var list)) {
					int idx = list.IndexOf(currentCheckpoint);
					if (idx >= 0 && idx + 1 < list.Count)
						nextCp = list[idx + 1];
					else if (idx == -1)
						nextCp = list.FirstOrDefault(id => id > currentCheckpoint);
					if (nextCp == 0) nextCp = null;
				}

				if (nextCp.HasValue) {
					ts.StepAction = StepActionKind.StepOver;
					ts.RunUntilCheckpointId = nextCp.Value;
				}
				else {
					ts.StepAction = StepActionKind.StepOver;
					ts.RunUntilCheckpointId = null;
					ts.PauseOnPopToDepth = Math.Max(0, ts.FrameStack.Count - 1);
				}

				ts.CurrentTcs?.TrySetResult(true);
			}
		}

		public void StepOut(int pauseId) {
			if (!TryGetThreadStateByPauseId(pauseId, out var ts)) return;
			lock (ts.Lock) {
				var currentDepth = ts.FrameStack.Count;
				if (currentDepth == 0) {
					ts.StepAction = StepActionKind.StepInto;
				}
				else {
					ts.StepAction = StepActionKind.StepOut;
					ts.PauseOnPopToDepth = Math.Max(0, currentDepth - 1);
				}
				ts.CurrentTcs?.TrySetResult(true);
			}
		}

		bool TryGetThreadStateByPauseId(int pauseId, out ThreadState? ts) {
			ts = threadStates.Values.FirstOrDefault(t => {
				lock (t.Lock) return t.CurrentPauseId == pauseId;
			});
			return ts != null;
		}

		class FrameInfo {
			public string MethodName = "";
			public IReadOnlyList<(string Name, object? Value)> Locals = Array.Empty<(string, object?)>();
			public int LastCheckpointId;
		}

		class ThreadState {
			public int ThreadId { get; }
			public object Lock { get; } = new object();
			public Stack<FrameInfo> FrameStack { get; } = new Stack<FrameInfo>();
			public StepActionKind StepAction { get; set; } = StepActionKind.None;
			public int? RunUntilCheckpointId { get; set; } = null;
			public int? PauseOnPopToDepth { get; set; } = null;
			public bool PauseNextCheckpoint { get; set; } = false;
			public TaskCompletionSource<bool>? CurrentTcs { get; set; }
			public int CurrentPauseId { get; set; }
			public ScriptFrame? LastPausedFrame { get; set; }

			public ThreadState(int threadId) { ThreadId = threadId; }

			public void ClearStepState() {
				StepAction = StepActionKind.None;
				RunUntilCheckpointId = null;
				PauseOnPopToDepth = null;
				PauseNextCheckpoint = false;
			}
		}
	}
}