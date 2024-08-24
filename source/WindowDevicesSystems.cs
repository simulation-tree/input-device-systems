using InputDevices.Events;
using SDL3;
using Simulation;
using System;
using static SDL3.SDL3;

namespace InputDevices.Systems
{
    public class WindowDevicesSystems : SystemBase
    {
        public WindowDevicesSystems(World world) : base(world)
        {
            Subscribe<InputUpdate>(Update);
        }

        public override void Dispose()
        {
            base.Dispose();
        }

        private void Update(InputUpdate update)
        {
            ReadSDL3DeviceInputs();
        }

        private unsafe void ReadSDL3DeviceInputs()
        {
            byte* state = SDL_GetKeyboardState(null);
            for (int j = 0; j < (int)SDL_Scancode.NumScancodes; j++)
            {
                byte key = state[j];
                if (key == 1)
                {
                    //Console.WriteLine($"Key {(Keyboard.Button)j} is pressed on keyboard");
                }
            }
        }
    }
}