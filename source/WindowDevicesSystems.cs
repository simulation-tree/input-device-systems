using Collections;
using InputDevices.Components;
using SDL3;
using Simulation;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows;
using Windows.Components;
using Worlds;
using static SDL3.SDL3;

namespace InputDevices.Systems
{
    //todo: perhaps split this system into one for each type of device?
    //tho then that will mean 3 individual event watchers
    public partial struct WindowDevicesSystems : ISystem
    {
        private readonly Dictionary<uint, VirtualDevice<KeyboardState>> keyboards;
        private readonly Dictionary<uint, VirtualDevice<MouseState>> mice;
        private readonly Simulator simulator;
        private readonly unsafe delegate* unmanaged[Cdecl]<nint, SDL_Event*, SDLBool> eventFilterFunction;

        private unsafe WindowDevicesSystems(Simulator simulator)
        {
            keyboards = new();
            mice = new();
            this.simulator = simulator;
            eventFilterFunction = &EventFilter;
            SDL_AddEventWatch(eventFilterFunction, simulator.Address);
        }

        void ISystem.Start(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                systemContainer.Write(new WindowDevicesSystems(systemContainer.Simulator));
            }
        }

        void ISystem.Update(in SystemContainer systemContainer, in World world, in TimeSpan delta)
        {
            if (systemContainer.World == world)
            {
                UpdateEntitiesToMatchDevices();
                AdvancePreviousStates();
            }
        }

        unsafe void ISystem.Finish(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                SDL_RemoveEventWatch(eventFilterFunction, simulator.Address);
                foreach (uint keyboardId in keyboards.Keys)
                {
                    ref VirtualDevice<KeyboardState> device = ref keyboards[keyboardId];
                    device.InputDevice.Dispose();
                }

                foreach (uint mouseId in mice.Keys)
                {
                    ref VirtualDevice<MouseState> device = ref mice[mouseId];
                    device.InputDevice.Dispose();
                }

                mice.Dispose();
                keyboards.Dispose();
            }
        }

        private readonly void UpdateEntitiesToMatchDevices()
        {
            foreach (uint keyboardId in keyboards.Keys)
            {
                ref VirtualDevice<KeyboardState> device = ref keyboards[keyboardId];
                if (device.entity == default)
                {
                    device.InputDevice = new Keyboard(device.World);
                }

                ref IsKeyboard current = ref device.World.GetComponent<IsKeyboard>(device.entity);
                ref LastKeyboardState last = ref device.World.GetComponent<LastKeyboardState>(device.entity);
                current.state = device.internalCurrentState;
                last.value = device.internalLastState;
            }

            foreach (uint mouseId in mice.Keys)
            {
                ref VirtualDevice<MouseState> device = ref mice[mouseId];
                if (device.entity == default)
                {
                    device.InputDevice = new Mouse(device.World);
                }

                ref IsMouse current = ref device.World.GetComponent<IsMouse>(device.entity);
                ref LastMouseState last = ref device.World.GetComponent<LastMouseState>(device.entity);
                current.state = device.internalCurrentState;
                last.value = device.internalLastState;
            }
        }

        private readonly void AdvancePreviousStates()
        {
            foreach (uint keyboardId in keyboards.Keys)
            {
                ref VirtualDevice<KeyboardState> device = ref keyboards[keyboardId];
                device.internalLastState = device.internalCurrentState;
            }

            foreach (uint mouseId in mice.Keys)
            {
                ref VirtualDevice<MouseState> device = ref mice[mouseId];
                device.internalLastState = device.internalCurrentState;
                device.internalCurrentState.scroll = default;
            }
        }

        private readonly void KeyboardEvent(Window window, SDL_EventType type, SDL_KeyboardDeviceEvent kdevice, SDL_KeyboardEvent key)
        {
            uint keyboardId = (uint)kdevice.which;
            if (type == SDL_EventType.KeyboardRemoved)
            {
                if (keyboards.TryRemove(keyboardId, out VirtualDevice<KeyboardState> removed))
                {
                    removed.InputDevice.Dispose();
                }
            }
            else
            {
                ref VirtualDevice<KeyboardState> device = ref keyboards.TryGetValue(keyboardId, out bool contains);
                if (!contains)
                {
                    device = ref keyboards.Add(keyboardId, new(window));
                }

                uint control = (uint)key.scancode;
                if (type == SDL_EventType.KeyDown)
                {
                    device.internalCurrentState[control] = true;
                }
                else if (type == SDL_EventType.KeyUp)
                {
                    device.internalCurrentState[control] = false;
                }
            }
        }

        private readonly void MouseEvent(Window window, SDL_EventType type, SDL_MouseDeviceEvent mdevice, SDL_MouseMotionEvent motion, SDL_MouseWheelEvent wheel, SDL_MouseButtonEvent button, SDL_WindowID windowId)
        {
            uint mouseId = (uint)mdevice.which;
            if (type == SDL_EventType.MouseRemoved)
            {
                if (mice.TryRemove(mouseId, out VirtualDevice<MouseState> removed))
                {
                    removed.InputDevice.Dispose();
                }
            }
            else
            {
                ref VirtualDevice<MouseState> device = ref mice.TryGetValue(mouseId, out bool contains);
                if (!contains)
                {
                    device = ref mice.Add(mouseId, new(window));
                }

                if (type == SDL_EventType.MouseMotion)
                {
                    Vector2 size = window.Size;
                    device.internalCurrentState.position = new(motion.x, size.Y - motion.y);
                }
                else if (type == SDL_EventType.MouseWheel)
                {
                    device.internalCurrentState.scroll = new(wheel.x, wheel.y);
                }
                else if (type == SDL_EventType.MouseButtonDown)
                {
                    uint control = button.button;
                    device.internalCurrentState[control] = true;
                }
                else if (type == SDL_EventType.MouseButtonUp)
                {
                    uint control = button.button;
                    device.internalCurrentState[control] = false;
                }
            }
        }

        public static bool TryGetWindow(Simulator simulator, uint id, out Window window)
        {
            foreach (World programWorld in simulator.ProgramWorlds)
            {
                ComponentQuery<IsWindow> query = new(programWorld);
                foreach (var r in query)
                {
                    ref IsWindow component = ref r.component1;
                    if (component.id == id)
                    {
                        window = new Entity(programWorld, r.entity).As<Window>();
                        return true;
                    }
                }
            }

            window = default;
            return false;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe SDLBool EventFilter(nint simulatorAddress, SDL_Event* sdlEvent)
        {
            SDL_EventType type = sdlEvent->type;
            if (type == SDL_EventType.MouseMotion || type == SDL_EventType.MouseWheel || type == SDL_EventType.MouseAdded ||
                type == SDL_EventType.MouseRemoved || type == SDL_EventType.MouseButtonDown || type == SDL_EventType.MouseButtonUp)
            {
                Simulator simulator = new(simulatorAddress);
                if (TryGetWindow(simulator, (uint)sdlEvent->window.windowID, out Window window))
                {
                    ref WindowDevicesSystems system = ref simulator.GetSystem<WindowDevicesSystems>().Value;
                    system.MouseEvent(window, type, sdlEvent->mdevice, sdlEvent->motion, sdlEvent->wheel, sdlEvent->button, sdlEvent->window.windowID);
                }
            }
            else if (type == SDL_EventType.KeyboardAdded || type == SDL_EventType.KeyboardRemoved || type == SDL_EventType.KeyDown || type == SDL_EventType.KeyUp)
            {
                Simulator simulator = new(simulatorAddress);
                if (TryGetWindow(simulator, (uint)sdlEvent->window.windowID, out Window window))
                {
                    ref WindowDevicesSystems system = ref simulator.GetSystem<WindowDevicesSystems>().Value;
                    system.KeyboardEvent(window, type, sdlEvent->kdevice, sdlEvent->key);
                }
            }

            return true;
        }

        public struct VirtualDevice<T> where T : unmanaged
        {
            public readonly Window window;
            public uint entity;
            public T internalCurrentState;
            public T internalLastState;

            public InputDevice InputDevice
            {
                readonly get
                {
                    return new(World, entity);
                }
                set
                {
                    entity = value.GetEntityValue();
                }
            }

            public readonly World World => window.GetWorld();

            public VirtualDevice(Window window)
            {
                this.window = window;
            }
        }
    }
}