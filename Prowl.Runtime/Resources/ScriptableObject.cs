﻿// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;

namespace Prowl.Runtime;

public abstract class ScriptableObject : EngineObject, ISerializationCallbackReceiver
{
    public ScriptableObject() : base() { }
    private ScriptableObject(string name) : base(name) { }

    // ScriptableObjects can only be created via the AssetDatabase loading them, so their guranteed to always Deserialize
    public void OnAfterDeserialize() => OnEnable();
    public void OnBeforeSerialize() { }

    public virtual void OnEnable() { }

}
