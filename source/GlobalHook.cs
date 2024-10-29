using SharpHook;
using System;
using System.Runtime.InteropServices;

namespace InputDevices.Systems
{
    public readonly struct GlobalHook : IDisposable, IEquatable<GlobalHook>
    {
        private readonly GCHandle handle;

        public GlobalHook(TaskPoolGlobalHook hook)
        {
            handle = GCHandle.Alloc(hook);
        }

        public void Dispose()
        {
            if (handle.IsAllocated)
            {
                TaskPoolGlobalHook hook = this;
                hook.Dispose();
                handle.Free();
            }
        }

        public readonly override bool Equals(object? obj)
        {
            return obj is GlobalHook hook && Equals(hook);
        }

        public readonly bool Equals(GlobalHook other)
        {
            return handle.Equals(other.handle);
        }

        public readonly override int GetHashCode()
        {
            return HashCode.Combine(handle);
        }

        public static bool operator ==(GlobalHook left, GlobalHook right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(GlobalHook left, GlobalHook right)
        {
            return !(left == right);
        }

        public static implicit operator TaskPoolGlobalHook(GlobalHook hook)
        {
            return (TaskPoolGlobalHook)(hook.handle.Target ?? throw new ObjectDisposedException(nameof(GlobalHook)));
        }
    }
}