namespace InputDevices.Systems
{
    internal struct Device<T> where T : unmanaged
    {
        public uint entity;
        public uint window;
        public T component;

        public Device(uint window)
        {
            entity = default;
            this.window = window;
            component = default;
        }
    }
}