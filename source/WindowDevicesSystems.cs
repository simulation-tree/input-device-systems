using Collections;
using InputDevices.Components;
using SDL3;
using Simulation;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unmanaged;
using Worlds;
using static SDL3.SDL3;

namespace InputDevices.Systems
{
    //todo: perhaps split this system into one for each type of device?
    //tho then that will mean 3 individual event watchers
    public partial struct WindowDevicesSystems : ISystem
    {
        private readonly Dictionary<uint, List<Keyboard>> keyboardEntities;
        private readonly Dictionary<uint, KeyboardState> currentKeyboards;
        private readonly Dictionary<uint, KeyboardState> lastKeyboards;
        private readonly Dictionary<uint, List<Mouse>> mouseEntities;
        private readonly Dictionary<uint, MouseState> currentMice;
        private readonly Dictionary<uint, MouseState> lastMice;
        private readonly Simulator simulator;
        private readonly unsafe delegate* unmanaged[Cdecl]<nint, SDL_Event*, SDLBool> eventFilterFunction;

        private unsafe WindowDevicesSystems(Simulator simulator)
        {
            keyboardEntities = new();
            currentKeyboards = new();
            lastKeyboards = new();
            mouseEntities = new();
            currentMice = new();
            lastMice = new();
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
                UpdateEntitiesToMatchDevices(systemContainer.Simulator);
                AdvancePreviousStates();
            }
        }

        unsafe void ISystem.Finish(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
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
        }

        private readonly void UpdateEntitiesToMatchDevices(Simulator simulator)
        {
            foreach (uint keyboardId in currentKeyboards.Keys)
            {
                USpan<Keyboard> keyboards = GetOrCreateKeyboard(simulator, keyboardId);
                foreach (ref Keyboard keyboard in keyboards)
                {
                    ref KeyboardState state = ref keyboard.AsEntity().GetComponent<IsKeyboard>().state;
                    ref KeyboardState lastState = ref keyboard.AsEntity().GetComponent<LastKeyboardState>().value;
                    state = currentKeyboards[keyboardId];
                    lastState = lastKeyboards[keyboardId];
                }
            }

            foreach (uint mouseId in currentMice.Keys)
            {
                USpan<Mouse> mice = GetOrCreateMouse(simulator, mouseId);
                foreach (ref Mouse mouse in mice)
                {
                    ref MouseState state = ref mouse.AsEntity().GetComponent<IsMouse>().state;
                    ref MouseState lastState = ref mouse.AsEntity().GetComponent<LastMouseState>().value;
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
                currentState.scroll = default;
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
                    currentState.position = new(motion.x, height - motion.y);
                }
                else if (type == SDL_EventType.MouseWheel)
                {
                    currentState.scroll = new(wheel.x, wheel.y);
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
            if (!keyboardEntities.TryGetValue(keyboardId, out List<Keyboard> keyboards))
            {
                keyboards = new();
                foreach (World programWorld in simulator.ProgramWorlds)
                {
                    Keyboard keyboard = new(programWorld);
                    keyboards.Add(keyboard);
                }

                keyboardEntities.Add(keyboardId, keyboards);
            }

            return keyboards.AsSpan();
        }

        private readonly USpan<Mouse> GetOrCreateMouse(Simulator simulator, uint mouseId)
        {
            if (!mouseEntities.TryGetValue(mouseId, out List<Mouse> mice))
            {
                mice = new();
                foreach (World programWorld in simulator.ProgramWorlds)
                {
                    Mouse mouse = new(programWorld);
                    mice.Add(mouse);
                }

                mouseEntities.Add(mouseId, mice);
            }

            return mice.AsSpan();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe SDLBool EventFilter(nint simulatorAddress, SDL_Event* sdlEvent)
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

            return true;
        }
    }
}