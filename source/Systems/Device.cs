namespace InputDevices.Systems
{
    internal struct Device<T> where T : unmanaged
    {
        public bool real;
        public uint deviceEntity;
        public uint windowEntity;
        public uint windowId;
        public T component;

        public Device(uint windowEntity, uint windowId)
        {
            real = true;
            deviceEntity = default;
            this.windowEntity = windowEntity;
            this.windowId = windowId;
            component = default;
        }
    }
}