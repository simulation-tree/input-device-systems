using Collections.Generic;
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
    public readonly partial struct WindowDevicesSystems : ISystem
    {
        private readonly Dictionary<uint, Window> windows;
        private readonly Dictionary<uint, VirtualDevice<KeyboardState>> keyboards;
        private readonly Dictionary<uint, VirtualDevice<MouseState>> mice;
        private readonly unsafe delegate* unmanaged[Cdecl]<nint, SDL_Event*, SDLBool> eventFilterFunction;

        private unsafe WindowDevicesSystems(delegate* unmanaged[Cdecl]<nint, SDL_Event*, SDLBool> eventFilterFunction)
        {
            windows = new();
            keyboards = new();
            mice = new();
            this.eventFilterFunction = eventFilterFunction;
        }

        public readonly void Dispose()
        {
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
            windows.Dispose();
        }

        unsafe void ISystem.Start(in SystemContext context, in World world)
        {
            if (context.IsSimulatorWorld(world))
            {
                delegate* unmanaged[Cdecl]<nint, SDL_Event*, SDLBool> eventFilterFunction = &EventFilter;
                SDL_AddEventWatch(eventFilterFunction, context.Simulator.Address);
                context.Write(new WindowDevicesSystems(eventFilterFunction));
            }
        }

        void ISystem.Update(in SystemContext context, in World world, in TimeSpan delta)
        {
            FindWindows(world);
            if (context.IsSimulatorWorld(world))
            {
                UpdateEntitiesToMatchDevices();
                AdvancePreviousStates();
            }
        }

        unsafe void ISystem.Finish(in SystemContext context, in World world)
        {
            if (context.IsSimulatorWorld(world))
            {
                SDL_RemoveEventWatch(eventFilterFunction, context.Simulator.Address);
            }
        }

        private readonly void FindWindows(World world)
        {
            //remove windows that no longer exist
            Span<uint> toRemove = stackalloc uint[windows.Count];
            int removeCount = 0;
            foreach ((uint windowId, Window window) in windows)
            {
                if (window.IsDestroyed)
                {
                    toRemove[removeCount++] = windowId;
                }
            }

            for (int i = 0; i < removeCount; i++)
            {
                windows.Remove(toRemove[i]);
            }

            //add windows
            int windowType = world.Schema.GetComponentType<IsWindow>();
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.ContainsComponent(windowType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsWindow> components = chunk.GetComponents<IsWindow>(windowType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsWindow component = ref components[i];
                        if (!windows.ContainsKey(component.id))
                        {
                            Entity entity = new(world, entities[i]);
                            windows.Add(component.id, entity.As<Window>());
                        }
                    }
                }
            }
        }

        private readonly void UpdateEntitiesToMatchDevices()
        {
            foreach (uint keyboardId in keyboards.Keys)
            {
                ref VirtualDevice<KeyboardState> device = ref keyboards[keyboardId];
                if (device.entity == default)
                {
                    Keyboard newKeyboard = new(device.World);
                    newKeyboard.Window = device.window;
                    device.InputDevice = newKeyboard;
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
                    Mouse newMouse = new(device.World);
                    newMouse.Window = device.window;
                    device.InputDevice = newMouse;
                }

                ref IsMouse current = ref device.World.GetComponent<IsMouse>(device.entity);
                ref LastMouseState last = ref device.World.GetComponent<LastMouseState>(device.entity);
                device.internalCurrentState.cursor = current.state.cursor;
                if (current.state.cursor != last.value.cursor)
                {
                    SDL_Cursor currentCursor = SDL_GetCursor();
                    SDL_DestroyCursor(currentCursor);

                    if (current.state.cursor == Mouse.Cursor.Default)
                    {
                        SDL_Cursor defaultCursor = SDL_GetDefaultCursor();
                        SDL_SetCursor(defaultCursor);
                    }
                    else
                    {
                        SDL_Cursor customCursor = SDL_CreateSystemCursor((SDL_SystemCursor)current.state.cursor);
                        SDL_SetCursor(customCursor);
                    }
                }

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
                device.internalCurrentState.delta = default;
            }
        }

        private readonly void KeyboardEvent(uint windowId, SDL_EventType type, SDL_KeyboardDeviceEvent kdevice, SDL_KeyboardEvent key)
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
                    if (windows.TryGetValue(windowId, out Window window))
                    {
                        device = ref keyboards.Add(keyboardId);
                        device = new(window);
                    }
                    else
                    {
                        //window doesnt exist yet? could happen if input system runs before a window system does
                        return;
                    }
                }

                uint control = (uint)key.scancode;
                if (type == SDL_EventType.KeyDown)
                {
                    device.internalCurrentState[(int)control] = true;
                }
                else if (type == SDL_EventType.KeyUp)
                {
                    device.internalCurrentState[(int)control] = false;
                }
            }
        }

        private readonly void MouseEvent(uint windowId, SDL_EventType type, SDL_MouseDeviceEvent mdevice, SDL_MouseMotionEvent motion, SDL_MouseWheelEvent wheel, SDL_MouseButtonEvent button)
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
                    if (windows.TryGetValue(windowId, out Window window))
                    {
                        device = ref mice.Add(mouseId);
                        device = new(window);
                    }
                    else
                    {
                        //window doesnt exist yet? could happen if input system runs before a window system does
                        return;
                    }
                }

                if (type == SDL_EventType.MouseMotion)
                {
                    Window window = windows[windowId];
                    Vector2 size = window.Size;
                    device.internalCurrentState.position = new(motion.x, size.Y - motion.y);
                    device.internalCurrentState.delta += new Vector2(motion.xrel, -motion.yrel);
                }
                else if (type == SDL_EventType.MouseWheel)
                {
                    device.internalCurrentState.scroll += new Vector2(wheel.x, wheel.y);
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

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe SDLBool EventFilter(nint simulatorAddress, SDL_Event* sdlEvent)
        {
            SDL_EventType type = sdlEvent->type;
            if (type is SDL_EventType.MouseMotion or SDL_EventType.MouseWheel || type == SDL_EventType.MouseAdded ||
                type == SDL_EventType.MouseRemoved || type == SDL_EventType.MouseButtonDown || type == SDL_EventType.MouseButtonUp)
            {
                Simulator simulator = new(simulatorAddress);
                WindowDevicesSystems system = simulator.GetSystem<WindowDevicesSystems>();
                uint windowId = (uint)sdlEvent->window.windowID;
                system.MouseEvent(windowId, type, sdlEvent->mdevice, sdlEvent->motion, sdlEvent->wheel, sdlEvent->button);
            }
            else if (type == SDL_EventType.KeyboardAdded || type == SDL_EventType.KeyboardRemoved || type == SDL_EventType.KeyDown || type == SDL_EventType.KeyUp)
            {
                Simulator simulator = new(simulatorAddress);
                WindowDevicesSystems system = simulator.GetSystem<WindowDevicesSystems>();
                uint windowId = (uint)sdlEvent->window.windowID;
                system.KeyboardEvent(windowId, type, sdlEvent->kdevice, sdlEvent->key);
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
                readonly get => new Entity(World, entity).As<InputDevice>();
                set => entity = value.value;
            }

            public readonly World World => window.world;

            public VirtualDevice(Window window)
            {
                this.window = window;
            }
        }
    }
}