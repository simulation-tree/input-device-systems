using System.Collections.Generic;

namespace InputDevices.Systems
{
    internal static class Systems
    {
        private static readonly List<WindowDevicesSystems> systems = new();

        public static nint Register(WindowDevicesSystems system)
        {
            systems.Add(system);
            return systems.Count - 1;
        }

        public static void Unregister(nint index)
        {
            systems.RemoveAt((int)index);
        }

        public static WindowDevicesSystems Get(nint index)
        {
            return systems[(int)index];
        }
    }
}