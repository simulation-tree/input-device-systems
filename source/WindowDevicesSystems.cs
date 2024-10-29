using InputDevices.Components;
using Programs.System;
using SDL3;
using Simulation;
using Simulation.Functions;
using System;
using System.Runtime.InteropServices;
using Unmanaged;
using Unmanaged.Collections;
using static SDL3.SDL3;

namespace InputDevices.Systems
{
    //todo: perhaps split this system into one for each type of device?
    //tho then that will mean 3 individual event watchers
    public struct WindowDevicesSystems : ISystem
    {
        private readonly UnmanagedDictionary<uint, UnmanagedList<Keyboard>> keyboardEntities;
        private readonly UnmanagedDictionary<uint, KeyboardState> currentKeyboards;
        private readonly UnmanagedDictionary<uint, KeyboardState> lastKeyboards;
        private readonly UnmanagedDictionary<uint, UnmanagedList<Mouse>> mouseEntities;
        private readonly UnmanagedDictionary<uint, MouseState> currentMice;
        private readonly UnmanagedDictionary<uint, MouseState> lastMice;
        private unsafe delegate* unmanaged<nint, SDL_Event*, SDL_bool> eventFilterFunction;

        readonly unsafe InitializeFunction ISystem.Initialize => new(&Initialize);
        readonly unsafe IterateFunction ISystem.Update => new(&Update);
        readonly unsafe FinalizeFunction ISystem.Finalize => new(&Finalize);

        [UnmanagedCallersOnly]
        private static void Initialize(SystemContainer container, World world)
        {
            if (container.World == world)
            {
                ref WindowDevicesSystems system = ref container.Read<WindowDevicesSystems>();
                system.Initialize(container.Simulator);
            }
        }

        [UnmanagedCallersOnly]
        private static void Update(SystemContainer container, World world, TimeSpan delta)
        {
            if (container.World == world)
            {
                ref WindowDevicesSystems system = ref container.Read<WindowDevicesSystems>();
                system.UpdateEntitiesToMatchDevices(container.Simulator);
                system.AdvancePreviousStates();
            }
        }

        [UnmanagedCallersOnly]
        private static void Finalize(SystemContainer container, World world)
        {
            if (container.World == world)
            {
                ref WindowDevicesSystems system = ref container.Read<WindowDevicesSystems>();
                system.CleanUp(container.Simulator);
            }
        }

        public unsafe WindowDevicesSystems()
        {
            keyboardEntities = new();
            currentKeyboards = new();
            lastKeyboards = new();
            mouseEntities = new();
            currentMice = new();
            lastMice = new();
        }

        private unsafe void Initialize(Simulator simulator)
        {
            eventFilterFunction = &EventFilter;
            SDL_AddEventWatch(eventFilterFunction, simulator.Address);
        }

        private readonly unsafe void CleanUp(Simulator simulator)
        {
            SDL_RemoveEventWatch(eventFilterFunction, simulator.Address);
            foreach (uint keyboardId in keyboardEntities.Keys)
            {
                keyboardEntities[keyboardId].Dispose();
            }

            foreach (uint mouseId in mouseEntities.Keys)
            {
                mouseEntities[mouseId].Dispose();
            }

            mouseEntities.Dispose();
            currentMice.Dispose();
            lastMice.Dispose();
            keyboardEntities.Dispose();
            currentKeyboards.Dispose();
            lastKeyboards.Dispose();
        }

        private readonly void UpdateEntitiesToMatchDevices(Simulator simulator)
        {
            foreach (uint keyboardId in currentKeyboards.Keys)
            {
                USpan<Keyboard> keyboards = GetOrCreateKeyboard(simulator, keyboardId);
                foreach (ref Keyboard keyboard in keyboards)
                {
                    ref KeyboardState state = ref keyboard.AsEntity().GetComponentRef<IsKeyboard>().state;
                    ref KeyboardState lastState = ref keyboard.AsEntity().GetComponentRef<LastKeyboardState>().value;
                    state = currentKeyboards[keyboardId];
                    lastState = lastKeyboards[keyboardId];
                }
            }

            foreach (uint mouseId in currentMice.Keys)
            {
                USpan<Mouse> mice = GetOrCreateMouse(simulator, mouseId);
                foreach (ref Mouse mouse in mice)
                {
                    ref MouseState state = ref mouse.AsEntity().GetComponentRef<IsMouse>().state;
                    ref MouseState lastState = ref mouse.AsEntity().GetComponentRef<LastMouseState>().value;
                    state = currentMice[mouseId];
                    lastState = lastMice[mouseId];
                }
            }
        }

        private readonly void AdvancePreviousStates()
        {
            foreach (uint keyboardId in currentKeyboards.Keys)
            {
                ref KeyboardState lastState = ref lastKeyboards[keyboardId];
                lastState = currentKeyboards[keyboardId];
            }

            foreach (uint mouseId in currentMice.Keys)
            {
                ref MouseState lastState = ref lastMice[mouseId];
                lastState = currentMice[mouseId];

                ref MouseState currentState = ref currentMice[mouseId];
                currentState.scrollX = default;
                currentState.scrollY = default;
            }
        }

        private readonly void KeyboardEvent(Simulator simulator, SDL_EventType type, SDL_KeyboardDeviceEvent kdevice, SDL_KeyboardEvent key)
        {
            uint keyboardId = (uint)kdevice.which;
            if (type == SDL_EventType.KeyboardRemoved)
            {
                if (currentKeyboards.TryRemove(keyboardId, out _))
                {
                    lastKeyboards.Remove(keyboardId);
                }
            }
            else
            {
                if (!currentKeyboards.ContainsKey(keyboardId))
                {
                    currentKeyboards.Add(keyboardId, new());
                    lastKeyboards.Add(keyboardId, new());
                }

                uint control = (uint)key.scancode;
                ref KeyboardState currentState = ref currentKeyboards[keyboardId];
                if (type == SDL_EventType.KeyDown)
                {
                    currentState[control] = true;
                }
                else if (type == SDL_EventType.KeyUp)
                {
                    currentState[control] = false;
                }
            }
        }

        private readonly void MouseEvent(Simulator simulator, SDL_EventType type, SDL_MouseDeviceEvent mdevice, SDL_MouseMotionEvent motion, SDL_MouseWheelEvent wheel, SDL_MouseButtonEvent button, SDL_WindowID windowId)
        {
            uint mouseId = (uint)mdevice.which;
            if (type == SDL_EventType.MouseRemoved)
            {
                if (currentMice.TryRemove(mouseId, out _))
                {
                    lastMice.Remove(mouseId);
                }
            }
            else
            {
                if (!currentMice.ContainsKey(mouseId))
                {
                    currentMice.Add(mouseId, new());
                    lastMice.Add(mouseId, new());
                }

                ref MouseState currentState = ref currentMice[mouseId];
                if (type == SDL_EventType.MouseMotion)
                {
                    SDL_Window window = SDL_GetWindowFromID(windowId);
                    SDL_GetWindowSize(window, out _, out int height);
                    currentState.positionX = (int)motion.x;
                    currentState.positionY = height - (int)motion.y;
                }
                else if (type == SDL_EventType.MouseWheel)
                {
                    currentState.scrollX = (int)wheel.x;
                    currentState.scrollY = (int)wheel.y;
                }
                else if (type == SDL_EventType.MouseButtonDown)
                {
                    uint control = button.button;
                    currentState[control] = true;
                }
                else if (type == SDL_EventType.MouseButtonUp)
                {
                    uint control = button.button;
                    currentState[control] = false;
                }
            }
        }

        private readonly USpan<Keyboard> GetOrCreateKeyboard(Simulator simulator, uint keyboardId)
        {
            if (!keyboardEntities.TryGetValue(keyboardId, out UnmanagedList<Keyboard> keyboards))
            {
                keyboards = new();
                foreach (ProgramContainer program in simulator.Programs)
                {
                    Keyboard keyboard = new(program.programWorld);
                    keyboards.Add(keyboard);
                }

                keyboardEntities.Add(keyboardId, keyboards);
            }

            return keyboards.AsSpan();
        }

        private readonly USpan<Mouse> GetOrCreateMouse(Simulator simulator, uint mouseId)
        {
            if (!mouseEntities.TryGetValue(mouseId, out UnmanagedList<Mouse> mice))
            {
                mice = new();
                foreach (ProgramContainer program in simulator.Programs)
                {
                    Mouse mouse = new(program.programWorld);
                    mice.Add(mouse);
                }

                mouseEntities.Add(mouseId, mice);
            }

            return mice.AsSpan();
        }

        [UnmanagedCallersOnly]
        private static unsafe SDL_bool EventFilter(nint simulatorAddress, SDL_Event* sdlEvent)
        {
            SDL_EventType type = sdlEvent->type;
            if (type == SDL_EventType.MouseMotion || type == SDL_EventType.MouseWheel || type == SDL_EventType.MouseAdded ||
                type == SDL_EventType.MouseRemoved || type == SDL_EventType.MouseButtonDown || type == SDL_EventType.MouseButtonUp)
            {
                Simulator simulator = new(simulatorAddress);
                ref WindowDevicesSystems system = ref simulator.GetSystem<WindowDevicesSystems>().Value;
                system.MouseEvent(simulator, type, sdlEvent->mdevice, sdlEvent->motion, sdlEvent->wheel, sdlEvent->button, sdlEvent->window.windowID);
            }
            else if (type == SDL_EventType.KeyboardAdded || type == SDL_EventType.KeyboardRemoved || type == SDL_EventType.KeyDown || type == SDL_EventType.KeyUp)
            {
                Simulator simulator = new(simulatorAddress);
                ref WindowDevicesSystems system = ref simulator.GetSystem<WindowDevicesSystems>().Value;
                system.KeyboardEvent(simulator, type, sdlEvent->kdevice, sdlEvent->key);
            }

            return SDL_bool.SDL_TRUE;
        }
    }
}