# Fusion Component Serialization

This document describes how Unity MCP handles serialization of Photon Fusion networking components.

## Problem

Fusion networking components (`NetworkBehaviour`, `NetworkObject`, `NetworkTransform`, etc.) can crash Unity when their properties are accessed via reflection in edit mode. This is because many Fusion properties depend on runtime state (NetworkRunner, simulation tick, etc.) that doesn't exist outside of Play mode.

## Solution

The `GameObjectSerializer` class includes special handling for Fusion types:

1. **Detection**: Components are identified as Fusion types by checking their namespace (`Fusion`, `Mirror`, `Unity.Netcode`) or type name (`NetworkBehaviour`, `NetworkObject`, etc.)

2. **Safe Field Enumeration**: For Fusion components, only user-defined fields (using `BindingFlags.DeclaredOnly`) are serialized, avoiding inherited Fusion internals that could crash

3. **Fusion Type Value Extraction**: Special logic extracts meaningful values from Fusion structs

## Supported Fusion Types

### NetworkObject / NetworkBehaviour References
Serialized as object references with name and instanceID:
```json
{
  "name": "PlayerRig",
  "instanceID": 12345
}
```

### NetworkBool
Uses implicit conversion operator to serialize as boolean:
```json
false
```

### NetworkObjectGuid
Extracts `RawGuidValue` field:
```json
"a1b2c3d4e5f6..."
```

### NetworkObjectTypeId
Extracts Kind, IsValid, IsPrefab, and prefabId (when applicable):
```json
{
  "kind": "Prefab",
  "isValid": true,
  "isPrefab": true,
  "prefabId": "[Index:152]"
}
```

**Note**: On prefab assets in edit mode, `NetworkTypeId` shows as `Invalid` because the actual type ID is only populated at spawn time from the PrefabTable. This is expected Fusion behavior.

| Context | NetworkTypeId |
|---------|---------------|
| Prefab asset (edit mode) | `kind: Invalid`, `isValid: false` |
| Spawned prefab (runtime) | `kind: Prefab`, `isValid: true` |
| Scene object (runtime) | `kind: SceneObject`, `isValid: true` |

### NetworkId, PlayerRef, Tick
Extracts `Raw` property and additional metadata:
```json
{
  "raw": 12345,
  "isValid": true
}
```

For PlayerRef:
```json
{
  "raw": 1,
  "playerId": 1,
  "isRealPlayer": true
}
```

### Fusion Enums
Serialized as integers (consistent with Unity's default enum serialization):
```json
1
```

### NetworkBehaviour[] / NetworkObject[]
Serialized as arrays of object references:
```json
[
  { "name": "Component1", "instanceID": 111 },
  { "name": "Component2", "instanceID": 222 },
  null
]
```

### Null References
Null Fusion field values serialize as `null`:
```json
{
  "hardwareRig": null,
  "leftGrabber": null
}
```

## Unsupported Types

If a Fusion type cannot be serialized meaningfully, it is **omitted entirely** from the output rather than showing error messages. This keeps the serialization output clean and focused on useful data.

## Implementation Details

Key methods in `GameObjectSerializer.cs`:

- `IsFusionOrNetworkingTypeFast()`: Fast detection of Fusion/networking types by namespace and type name
- `IsNetworkBehaviourType()`: Checks if a type inherits from NetworkBehaviour
- `TrySerializeFusionValue()`: Attempts to extract meaningful data from Fusion structs using reflection
- `GetComponentData()`: Main entry point with special Fusion component handling

The serializer uses reflection to access:
- Implicit conversion operators (`op_Implicit`) for types like NetworkBool
- Public properties (`Value`, `Raw`, `Kind`, `IsValid`, etc.)
- Public fields (`RawGuidValue`, etc.)

## Testing

To verify Fusion serialization is working:

1. Open a prefab with Fusion components (e.g., NetworkRig)
2. Use Unity MCP to query the component data
3. Verify Fusion fields show meaningful values (not "skipped" messages)
4. Enter Play mode and spawn a networked object
5. Query the spawned instance to see runtime values (e.g., valid NetworkTypeId)
