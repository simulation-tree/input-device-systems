using Collections.Generic;
using InputDevices.Components;
using InputDevices.Messages;
using Rendering.Components;
using SDL3;
using Simulation;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Components;
using Worlds;
using static SDL3.SDL3;

namespace InputDevices.Systems
{
    [SkipLocalsInit]
    public partial class WindowDevicesSystems : SystemBase, IListener<InputUpdate>
    {
        private readonly World world;
        private readonly Dictionary<uint, uint> windows;
        private readonly Dictionary<uint, Vector2> windowSizes;
        private readonly Dictionary<uint, Device<IsKeyboard>> keyboards;
        private readonly Dictionary<uint, Device<IsMouse>> mice;
        private readonly List<uint> keyboardKeys;
        private readonly List<uint> mouseKeys;
        private readonly GCHandle handle;
        private unsafe delegate* unmanaged[Cdecl]<nint, SDL_Event*, SDLBool> eventFilterFunction;
        private readonly int keyboardType;
        private readonly int mouseType;
        private readonly int timestampType;
        private readonly int windowType;
        private readonly int destinationType;

        public WindowDevicesSystems(Simulator simulator, World world) : base(simulator)
        {
            handle = GCHandle.Alloc(this, GCHandleType.Normal);
            this.world = world;
            windows = new(8);
            windowSizes = new(8);
            keyboards = new(8);
            mice = new(8);
            keyboardKeys = new(8);
            mouseKeys = new(8);

            Schema schema = world.Schema;
            keyboardType = schema.GetComponentType<IsKeyboard>();
            mouseType = schema.GetComponentType<IsMouse>();
            timestampType = schema.GetComponentType<LastDeviceUpdateTime>();
            windowType = schema.GetComponentType<IsWindow>();
            destinationType = schema.GetComponentType<IsDestination>();
            AddListener();
        }

        public override void Dispose()
        {
            RemoveListener();

            foreach (Device<IsKeyboard> keyboard in keyboards.Values)
            {
                world.DestroyEntity(keyboard.entity);
            }

            foreach (Device<IsMouse> mouse in mice.Values)
            {
                world.DestroyEntity(mouse.entity);
            }

            mouseKeys.Dispose();
            keyboardKeys.Dispose();
            mice.Dispose();
            keyboards.Dispose();
            windowSizes.Dispose();
            windows.Dispose();
            handle.Free();
        }

        private unsafe void AddListener()
        {
            eventFilterFunction = &EventFilter;
            SDL_AddEventWatch(eventFilterFunction, GCHandle.ToIntPtr(handle));
        }

        private unsafe void RemoveListener()
        {
            SDL_RemoveEventWatch(eventFilterFunction, GCHandle.ToIntPtr(handle));
        }

        void IListener<InputUpdate>.Receive(ref InputUpdate message)
        {
            FindWindows();
            UpdateEntitiesToMatchDevices();
            AdvancePreviousStates();
        }

        private void FindWindows()
        {
            //remove windows that no longer exist
            Span<uint> toRemove = stackalloc uint[windows.Count];
            int removeCount = 0;
            foreach ((uint windowId, uint windowEntity) in windows)
            {
                if (!world.ContainsEntity(windowEntity))
                {
                    toRemove[removeCount++] = windowId;
                }
            }

            for (int i = 0; i < removeCount; i++)
            {
                windows.Remove(toRemove[i]);
                windowSizes.Remove(toRemove[i]);
            }

            //add windows and gather their sizes
            BitMask componentTypes = new(windowType, destinationType);
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.componentTypes.ContainsAll(componentTypes))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsWindow> windowComponents = chunk.GetComponents<IsWindow>(windowType);
                    ComponentEnumerator<IsDestination> destinationComponents = chunk.GetComponents<IsDestination>(destinationType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsWindow component = ref windowComponents[i];
                        ref IsDestination destination = ref destinationComponents[i];
                        ref Vector2 windowSize = ref windowSizes.TryGetValue(component.id, out bool contains);
                        if (!contains)
                        {
                            windowSize = ref windowSizes.Add(component.id);
                            windows.Add(component.id, entities[i]);
                        }

                        windowSize.X = destination.width;
                        windowSize.Y = destination.height;
                    }
                }
            }
        }

        private void UpdateEntitiesToMatchDevices()
        {
            Span<uint> keyboardKeysSpan = keyboardKeys.AsSpan();
            foreach (uint keyboardId in keyboardKeysSpan)
            {
                ref Device<IsKeyboard> keyboard = ref keyboards[keyboardId];
                if (keyboard.entity == default)
                {
                    Keyboard newKeyboard = new(world, (rint)1);
                    newKeyboard.AddReference(keyboard.window);
                    keyboard.entity = newKeyboard.value;
                }

                ref IsKeyboard component = ref world.GetComponent<IsKeyboard>(keyboard.entity, keyboardType);
                component.currentState = keyboard.component.currentState;
                component.lastState = keyboard.component.lastState;
            }

            Span<uint> mouseKeysSpan = mouseKeys.AsSpan();
            foreach (uint mouseId in mouseKeysSpan)
            {
                ref Device<IsMouse> mouse = ref mice[mouseId];
                if (mouse.entity == default)
                {
                    Mouse newMouse = new(world, (rint)1);
                    newMouse.AddReference(mouse.window);
                    mouse.entity = newMouse.value;
                }

                ref IsMouse component = ref world.GetComponent<IsMouse>(mouse.entity, mouseType);
                mouse.component.currentState.cursor = component.currentState.cursor;
                if (component.currentState.cursor != component.lastState.cursor)
                {
                    SDL_Cursor currentCursor = SDL_GetCursor();
                    SDL_DestroyCursor(currentCursor);

                    if (component.currentState.cursor == Mouse.Cursor.Default)
                    {
                        SDL_Cursor defaultCursor = SDL_GetDefaultCursor();
                        SDL_SetCursor(defaultCursor);
                    }
                    else
                    {
                        SDL_Cursor customCursor = SDL_CreateSystemCursor((SDL_SystemCursor)component.currentState.cursor);
                        SDL_SetCursor(customCursor);
                    }
                }

                component.currentState = mouse.component.currentState;
                component.lastState = mouse.component.lastState;
            }
        }

        private void AdvancePreviousStates()
        {
            Span<uint> keyboardKeysSpan = keyboardKeys.AsSpan();
            foreach (uint keyboardId in keyboardKeysSpan)
            {
                ref Device<IsKeyboard> device = ref keyboards[keyboardId];
                device.component.lastState = device.component.currentState;
            }

            Span<uint> mouseKeysSpan = mouseKeys.AsSpan();
            foreach (uint mouseId in mouseKeysSpan)
            {
                ref Device<IsMouse> device = ref mice[mouseId];
                device.component.lastState = device.component.currentState;
                device.component.currentState.scroll = default;
                device.component.currentState.delta = default;
            }
        }

        private void KeyboardEvent(uint windowId, SDL_EventType type, SDL_KeyboardDeviceEvent kdevice, SDL_KeyboardEvent key)
        {
            uint keyboardId = (uint)kdevice.which;
            if (type == SDL_EventType.KeyboardRemoved)
            {
                if (keyboards.TryRemove(keyboardId, out Device<IsKeyboard> removed))
                {
                    world.DestroyEntity(removed.entity);
                    keyboardKeys.Remove(keyboardId);
                }
            }
            else
            {
                ref Device<IsKeyboard> device = ref keyboards.TryGetValue(keyboardId, out bool contains);
                if (!contains)
                {
                    if (windows.TryGetValue(windowId, out uint window))
                    {
                        device = ref keyboards.Add(keyboardId);
                        device = new(window);
                        keyboardKeys.Add(keyboardId);
                    }
                    else
                    {
                        //window doesnt exist yet? could happen if input system runs before a window system does
                        return;
                    }
                }

                if (type == SDL_EventType.KeyDown)
                {
                    device.component.currentState[(int)key.scancode] = true;
                }
                else if (type == SDL_EventType.KeyUp)
                {
                    device.component.currentState[(int)key.scancode] = false;
                }
            }
        }

        private void MouseEvent(uint windowId, SDL_EventType type, SDL_MouseDeviceEvent mdevice, SDL_MouseMotionEvent motion, SDL_MouseWheelEvent wheel, SDL_MouseButtonEvent button)
        {
            uint mouseId = (uint)mdevice.which;
            if (type == SDL_EventType.MouseRemoved)
            {
                if (mice.TryRemove(mouseId, out Device<IsMouse> removed))
                {
                    world.DestroyEntity(removed.entity);
                    mouseKeys.Remove(mouseId);
                }
            }
            else
            {
                ref Device<IsMouse> device = ref mice.TryGetValue(mouseId, out bool contains);
                if (!contains)
                {
                    if (windows.TryGetValue(windowId, out uint window))
                    {
                        device = ref mice.Add(mouseId);
                        device = new(window);
                        mouseKeys.Add(mouseId);
                    }
                    else
                    {
                        //window doesnt exist yet? could happen if input system runs before a window system does
                        return;
                    }
                }

                if (type == SDL_EventType.MouseMotion)
                {
                    uint window = windows[windowId];
                    Vector2 size = windowSizes[windowId];
                    device.component.currentState.position = new(motion.x, size.Y - motion.y);
                    device.component.currentState.delta += new Vector2(motion.xrel, -motion.yrel);
                }
                else if (type == SDL_EventType.MouseWheel)
                {
                    device.component.currentState.scroll += new Vector2(wheel.x, wheel.y);
                }
                else if (type == SDL_EventType.MouseButtonDown)
                {
                    device.component.currentState[button.button] = true;
                }
                else if (type == SDL_EventType.MouseButtonUp)
                {
                    device.component.currentState[button.button] = false;
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe SDLBool EventFilter(nint handle, SDL_Event* sdlEvent)
        {
            SDL_EventType type = sdlEvent->type;
            if (type is SDL_EventType.MouseMotion or SDL_EventType.MouseWheel || type == SDL_EventType.MouseAdded ||
                type == SDL_EventType.MouseRemoved || type == SDL_EventType.MouseButtonDown || type == SDL_EventType.MouseButtonUp)
            {
                WindowDevicesSystems system = (WindowDevicesSystems)(GCHandle.FromIntPtr(handle).Target ?? throw new InvalidOperationException("Invalid handle"));
                uint windowId = (uint)sdlEvent->window.windowID;
                system.MouseEvent(windowId, type, sdlEvent->mdevice, sdlEvent->motion, sdlEvent->wheel, sdlEvent->button);
            }
            else if (type == SDL_EventType.KeyboardAdded || type == SDL_EventType.KeyboardRemoved || type == SDL_EventType.KeyDown || type == SDL_EventType.KeyUp)
            {
                WindowDevicesSystems system = (WindowDevicesSystems)(GCHandle.FromIntPtr(handle).Target ?? throw new InvalidOperationException("Invalid handle"));
                uint windowId = (uint)sdlEvent->window.windowID;
                system.KeyboardEvent(windowId, type, sdlEvent->kdevice, sdlEvent->key);
            }

            return true;
        }
    }
}