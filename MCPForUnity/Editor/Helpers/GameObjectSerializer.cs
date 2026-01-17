using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Runtime.Serialization; // For Converters
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Handles serialization of GameObjects and Components for MCP responses.
    /// Includes reflection helpers and caching for performance.
    /// </summary> 
    public static class GameObjectSerializer
    {
        // --- Data Serialization ---

        /// <summary>
        /// Creates a serializable representation of a GameObject.
        /// </summary>
        public static object GetGameObjectData(GameObject go)
        {
            if (go == null)
                return null;
            return new
            {
                name = go.name,
                instanceID = go.GetInstanceID(),
                tag = go.tag,
                layer = go.layer,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                isStatic = go.isStatic,
                scenePath = go.scene.path, // Identify which scene it belongs to
                transform = new // Serialize transform components carefully to avoid JSON issues
                {
                    // Serialize Vector3 components individually to prevent self-referencing loops.
                    // The default serializer can struggle with properties like Vector3.normalized.
                    position = new
                    {
                        x = go.transform.position.x,
                        y = go.transform.position.y,
                        z = go.transform.position.z,
                    },
                    localPosition = new
                    {
                        x = go.transform.localPosition.x,
                        y = go.transform.localPosition.y,
                        z = go.transform.localPosition.z,
                    },
                    rotation = new
                    {
                        x = go.transform.rotation.eulerAngles.x,
                        y = go.transform.rotation.eulerAngles.y,
                        z = go.transform.rotation.eulerAngles.z,
                    },
                    localRotation = new
                    {
                        x = go.transform.localRotation.eulerAngles.x,
                        y = go.transform.localRotation.eulerAngles.y,
                        z = go.transform.localRotation.eulerAngles.z,
                    },
                    scale = new
                    {
                        x = go.transform.localScale.x,
                        y = go.transform.localScale.y,
                        z = go.transform.localScale.z,
                    },
                    forward = new
                    {
                        x = go.transform.forward.x,
                        y = go.transform.forward.y,
                        z = go.transform.forward.z,
                    },
                    up = new
                    {
                        x = go.transform.up.x,
                        y = go.transform.up.y,
                        z = go.transform.up.z,
                    },
                    right = new
                    {
                        x = go.transform.right.x,
                        y = go.transform.right.y,
                        z = go.transform.right.z,
                    },
                },
                parentInstanceID = go.transform.parent?.gameObject.GetInstanceID() ?? 0, // 0 if no parent
                // Optionally include components, but can be large
                // components = go.GetComponents<Component>().Select(c => GetComponentData(c)).ToList()
                // Or just component names:
                componentNames = go.GetComponents<Component>()
                    .Select(c => c.GetType().FullName)
                    .ToList(),
            };
        }

        // --- Metadata Caching for Reflection ---
        private class CachedMetadata
        {
            public readonly List<PropertyInfo> SerializableProperties;
            public readonly List<FieldInfo> SerializableFields;

            public CachedMetadata(List<PropertyInfo> properties, List<FieldInfo> fields)
            {
                SerializableProperties = properties;
                SerializableFields = fields;
            }
        }
        // Key becomes Tuple<Type, bool>
        private static readonly Dictionary<Tuple<Type, bool>, CachedMetadata> _metadataCache = new Dictionary<Tuple<Type, bool>, CachedMetadata>();
        // --- End Metadata Caching ---

        /// <summary>
        /// Checks if a type is or derives from a type with the specified full name.
        /// Used to detect special-case components including their subclasses.
        /// </summary>
        private static bool IsOrDerivedFrom(Type type, string baseTypeFullName)
        {
            Type current = type;
            while (current != null)
            {
                if (current.FullName == baseTypeFullName)
                    return true;
                current = current.BaseType;
            }
            return false;
        }

        /// <summary>
        /// Serializes a UnityEngine.Object reference to a dictionary with name, instanceID, and assetPath.
        /// Used for consistent serialization of asset references in special-case component handlers.
        /// </summary>
        /// <param name="obj">The Unity object to serialize</param>
        /// <param name="includeAssetPath">Whether to include the asset path (default true)</param>
        /// <returns>A dictionary with the object's reference info, or null if obj is null</returns>
        private static Dictionary<string, object> SerializeAssetReference(UnityEngine.Object obj, bool includeAssetPath = true)
        {
            if (obj == null) return null;
            
            var result = new Dictionary<string, object>
            {
                { "name", obj.name },
                { "instanceID", obj.GetInstanceID() }
            };
            
            if (includeAssetPath)
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                result["assetPath"] = string.IsNullOrEmpty(assetPath) ? null : assetPath;
            }
            
            return result;
        }

        /// <summary>
        /// Checks if a type is or inherits from NetworkBehaviour (Fusion networking).
        /// Uses type name matching to avoid requiring Fusion assembly reference.
        /// </summary>
        private static bool IsNetworkBehaviourType(Type type)
        {
            if (type == null) return false;
            
            // Check the type itself and all base types
            Type currentType = type;
            while (currentType != null && currentType != typeof(MonoBehaviour) && currentType != typeof(Component) && currentType != typeof(object))
            {
                // Check if type name matches NetworkBehaviour (case-insensitive)
                if (currentType.Name.Equals("NetworkBehaviour", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                currentType = currentType.BaseType;
            }
            return false;
        }

        /// <summary>
        /// Creates a serializable representation of a Component, attempting to serialize
        /// public properties and fields using reflection, with caching and control over non-public fields.
        /// </summary>
        // Add the flag parameter here
        public static object GetComponentData(Component c, bool includeNonPublicSerializedFields = true)
        {
            // Trace logging (can be enabled for debugging)
            // McpLog.Info($"[GetComponentData] Processing: {c?.GetType()?.FullName ?? "null"}");

            if (c == null) return null;

            Type componentType = c.GetType();

            // --- Special handling for Transform to avoid reflection crashes and problematic properties --- 
            if (componentType == typeof(Transform))
            {
                Transform tr = c as Transform;
                // McpLog.Info($"[GetComponentData] Manually serializing Transform (ID: {tr.GetInstanceID()})");
                return new Dictionary<string, object>
                {
                    { "typeName", componentType.FullName },
                    { "instanceID", tr.GetInstanceID() },
                    // Manually extract known-safe properties. Avoid Quaternion 'rotation' and 'lossyScale'.
                    { "position", CreateTokenFromValue(tr.position, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "localPosition", CreateTokenFromValue(tr.localPosition, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "eulerAngles", CreateTokenFromValue(tr.eulerAngles, typeof(Vector3))?.ToObject<object>() ?? new JObject() }, // Use Euler angles
                    { "localEulerAngles", CreateTokenFromValue(tr.localEulerAngles, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "localScale", CreateTokenFromValue(tr.localScale, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "right", CreateTokenFromValue(tr.right, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "up", CreateTokenFromValue(tr.up, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "forward", CreateTokenFromValue(tr.forward, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "parentInstanceID", tr.parent?.gameObject.GetInstanceID() ?? 0 },
                    { "rootInstanceID", tr.root?.gameObject.GetInstanceID() ?? 0 },
                    { "childCount", tr.childCount },
                    // Include standard Object/Component properties
                    { "name", tr.name },
                    { "tag", tr.tag },
                    { "gameObjectInstanceID", tr.gameObject?.GetInstanceID() ?? 0 }
                };
            }
            // --- End Special handling for Transform --- 

            // --- Special handling for Camera to avoid matrix-related crashes ---
            if (componentType == typeof(Camera))
            {
                Camera cam = c as Camera;
                var cameraProperties = new Dictionary<string, object>();

                // List of safe properties to serialize
                var safeProperties = new Dictionary<string, Func<object>>
                {
                    { "nearClipPlane", () => cam.nearClipPlane },
                    { "farClipPlane", () => cam.farClipPlane },
                    { "fieldOfView", () => cam.fieldOfView },
                    { "renderingPath", () => (int)cam.renderingPath },
                    { "actualRenderingPath", () => (int)cam.actualRenderingPath },
                    { "allowHDR", () => cam.allowHDR },
                    { "allowMSAA", () => cam.allowMSAA },
                    { "allowDynamicResolution", () => cam.allowDynamicResolution },
                    { "forceIntoRenderTexture", () => cam.forceIntoRenderTexture },
                    { "orthographicSize", () => cam.orthographicSize },
                    { "orthographic", () => cam.orthographic },
                    { "opaqueSortMode", () => (int)cam.opaqueSortMode },
                    { "transparencySortMode", () => (int)cam.transparencySortMode },
                    { "depth", () => cam.depth },
                    { "aspect", () => cam.aspect },
                    { "cullingMask", () => cam.cullingMask },
                    { "eventMask", () => cam.eventMask },
                    { "backgroundColor", () => cam.backgroundColor },
                    { "clearFlags", () => (int)cam.clearFlags },
                    { "stereoEnabled", () => cam.stereoEnabled },
                    { "stereoSeparation", () => cam.stereoSeparation },
                    { "stereoConvergence", () => cam.stereoConvergence },
                    { "enabled", () => cam.enabled },
                    { "name", () => cam.name },
                    { "tag", () => cam.tag },
                    { "gameObject", () => new { name = cam.gameObject.name, instanceID = cam.gameObject.GetInstanceID() } }
                };

                foreach (var prop in safeProperties)
                {
                    try
                    {
                        var value = prop.Value();
                        if (value != null)
                        {
                            AddSerializableValue(cameraProperties, prop.Key, value.GetType(), value);
                        }
                    }
                    catch (Exception)
                    {
                        // Silently skip any property that fails
                        continue;
                    }
                }

                return new Dictionary<string, object>
                {
                    { "typeName", componentType.FullName },
                    { "instanceID", cam.GetInstanceID() },
                    { "properties", cameraProperties }
                };
            }
            // --- End Special handling for Camera ---

            // --- Special handling for UIDocument to avoid infinite loops from VisualElement hierarchy (Issue #585) ---
            // UIDocument.rootVisualElement contains circular parent/child references that cause infinite serialization loops.
            // Use IsOrDerivedFrom to also catch subclasses of UIDocument.
            if (IsOrDerivedFrom(componentType, "UnityEngine.UIElements.UIDocument"))
            {
                var uiDocProperties = new Dictionary<string, object>();

                try
                {
                    // Get panelSettings reference safely
                    var panelSettingsProp = componentType.GetProperty("panelSettings");
                    if (panelSettingsProp != null)
                    {
                        var panelSettings = panelSettingsProp.GetValue(c) as UnityEngine.Object;
                        uiDocProperties["panelSettings"] = SerializeAssetReference(panelSettings);
                    }

                    // Get visualTreeAsset reference safely (the UXML file)
                    var visualTreeAssetProp = componentType.GetProperty("visualTreeAsset");
                    if (visualTreeAssetProp != null)
                    {
                        var visualTreeAsset = visualTreeAssetProp.GetValue(c) as UnityEngine.Object;
                        uiDocProperties["visualTreeAsset"] = SerializeAssetReference(visualTreeAsset);
                    }

                    // Get sortingOrder safely
                    var sortingOrderProp = componentType.GetProperty("sortingOrder");
                    if (sortingOrderProp != null)
                    {
                        uiDocProperties["sortingOrder"] = sortingOrderProp.GetValue(c);
                    }

                    // Get enabled state (from Behaviour base class)
                    var enabledProp = componentType.GetProperty("enabled");
                    if (enabledProp != null)
                    {
                        uiDocProperties["enabled"] = enabledProp.GetValue(c);
                    }

                    // Get parentUI reference safely (no asset path needed - it's a scene reference)
                    var parentUIProp = componentType.GetProperty("parentUI");
                    if (parentUIProp != null)
                    {
                        var parentUI = parentUIProp.GetValue(c) as UnityEngine.Object;
                        uiDocProperties["parentUI"] = SerializeAssetReference(parentUI, includeAssetPath: false);
                    }

                    // NOTE: rootVisualElement is intentionally skipped - it contains circular
                    // parent/child references that cause infinite serialization loops
                    uiDocProperties["_note"] = "rootVisualElement skipped to prevent circular reference loops";
                }
                catch (Exception e)
                {
                    McpLog.Warn($"[GetComponentData] Error reading UIDocument properties: {e.Message}");
                }

                // Return structure matches Camera special handling (typeName, instanceID, properties)
                return new Dictionary<string, object>
                {
                    { "typeName", componentType.FullName },
                    { "instanceID", c.GetInstanceID() },
                    { "properties", uiDocProperties }
                };
            }
            // --- End Special handling for UIDocument ---

            // --- Special handling for ALL Fusion/Networking components to avoid crashes ---
            // Any component from Fusion, Mirror, or Netcode namespaces can crash when properties are accessed in edit mode
            bool isFusionType = IsFusionOrNetworkingTypeFast(componentType);

            if (isFusionType)
            {
                var fusionProperties = new Dictionary<string, object>();

                // Get basic info safely
                try { fusionProperties["name"] = c.name; } catch { }
                try { fusionProperties["instanceID"] = c.GetInstanceID(); } catch { }
                try { if (c is Behaviour b) fusionProperties["enabled"] = b.enabled; } catch { }
                try { fusionProperties["gameObjectInstanceID"] = c.gameObject?.GetInstanceID() ?? 0; } catch { }

                // Enumerate ONLY user-defined fields (not properties) that have safe types
                try
                {
                    // Only get fields declared on the user's class, not inherited Fusion fields
                    var userFields = componentType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                    foreach (var field in userFields)
                    {
                        // Skip backing fields
                        if (field.Name.EndsWith("k__BackingField")) continue;

                        // Skip non-public fields without [SerializeField]
                        if (!field.IsPublic && !field.IsDefined(typeof(SerializeField), true)) continue;

                        // Check if field TYPE is a Fusion type - try to serialize meaningfully
                        Type fieldType = field.FieldType;
                        if (IsFusionOrNetworkingTypeFast(fieldType))
                        {
                            try
                            {
                                object value = field.GetValue(c);
                                if (value == null)
                                {
                                    // Null values serialize as null (not skipped)
                                    fusionProperties[field.Name] = null;
                                    continue;
                                }

                                // Try to extract useful info from the Fusion type
                                object fusionSerialized = TrySerializeFusionValue(value, value.GetType());
                                if (fusionSerialized != null)
                                {
                                    fusionProperties[field.Name] = fusionSerialized;
                                    continue;
                                }
                            }
                            catch { /* Field access failed */ }

                            // If we couldn't serialize the Fusion type meaningfully, just skip it entirely
                            continue;
                        }

                        // Safe to get value
                        try
                        {
                            object value = field.GetValue(c);
                            if (value != null)
                            {
                                // Double-check the actual value type
                                Type actualType = value.GetType();
                                if (IsFusionOrNetworkingTypeFast(actualType))
                                {
                                    // Try to serialize the Fusion value
                                    object fusionSerialized = TrySerializeFusionValue(value, actualType);
                                    if (fusionSerialized != null)
                                    {
                                        fusionProperties[field.Name] = fusionSerialized;
                                    }
                                    // If we couldn't serialize the Fusion type meaningfully, just skip it entirely
                                    continue;
                                }
                            }
                            AddSerializableValueSafe(fusionProperties, field.Name, fieldType, value);
                        }
                        catch (Exception ex)
                        {
                            fusionProperties[field.Name] = $"<error: {ex.Message}>";
                        }
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[GetComponentData] Error enumerating Fusion component fields: {ex.Message}");
                }

                return new Dictionary<string, object>
                {
                    { "typeName", componentType.FullName },
                    { "instanceID", fusionProperties.ContainsKey("instanceID") ? fusionProperties["instanceID"] : 0 },
                    { "isFusionComponent", true },
                    { "properties", fusionProperties }
                };
            }
            // --- End Special handling for ALL Fusion/Networking components ---

            var data = new Dictionary<string, object>
            {
                { "typeName", componentType.FullName },
                { "instanceID", c.GetInstanceID() }
            };

            // --- Get Cached or Generate Metadata (using new cache key) ---
            Tuple<Type, bool> cacheKey = new Tuple<Type, bool>(componentType, includeNonPublicSerializedFields);
            if (!_metadataCache.TryGetValue(cacheKey, out CachedMetadata cachedData))
            {
                var propertiesToCache = new List<PropertyInfo>();
                var fieldsToCache = new List<FieldInfo>();

                // Traverse the hierarchy from the component type up to MonoBehaviour
                Type currentType = componentType;
                while (currentType != null && currentType != typeof(MonoBehaviour) && currentType != typeof(object))
                {
                    // Get properties declared only at the current type level
                    BindingFlags propFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    foreach (var propInfo in currentType.GetProperties(propFlags))
                    {
                        // Basic filtering (readable, not indexer, not transform which is handled elsewhere)
                        if (!propInfo.CanRead || propInfo.GetIndexParameters().Length > 0 || propInfo.Name == "transform") continue;
                        // Add if not already added (handles overrides - keep the most derived version)
                        if (!propertiesToCache.Any(p => p.Name == propInfo.Name))
                        {
                            propertiesToCache.Add(propInfo);
                        }
                    }

                    // Get fields declared only at the current type level (both public and non-public)
                    BindingFlags fieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    var declaredFields = currentType.GetFields(fieldFlags);

                    // Process the declared Fields for caching
                    foreach (var fieldInfo in declaredFields)
                    {
                        if (fieldInfo.Name.EndsWith("k__BackingField")) continue; // Skip backing fields

                        // Add if not already added (handles hiding - keep the most derived version)
                        if (fieldsToCache.Any(f => f.Name == fieldInfo.Name)) continue;

                        bool shouldInclude = false;
                        if (includeNonPublicSerializedFields)
                        {
                            // If TRUE, include Public OR any NonPublic with [SerializeField] (private/protected/internal)
                            var hasSerializeField = fieldInfo.IsDefined(typeof(SerializeField), inherit: true);
                            shouldInclude = fieldInfo.IsPublic || (!fieldInfo.IsPublic && hasSerializeField);
                        }
                        else // includeNonPublicSerializedFields is FALSE
                        {
                            // If FALSE, include ONLY if it is explicitly Public.
                            shouldInclude = fieldInfo.IsPublic;
                        }

                        if (shouldInclude)
                        {
                            fieldsToCache.Add(fieldInfo);
                        }
                    }

                    // Move to the base type
                    currentType = currentType.BaseType;
                }
                // --- End Hierarchy Traversal ---

                cachedData = new CachedMetadata(propertiesToCache, fieldsToCache);
                _metadataCache[cacheKey] = cachedData; // Add to cache with combined key
            }
            // --- End Get Cached or Generate Metadata ---

            // --- Use cached metadata ---
            var serializablePropertiesOutput = new Dictionary<string, object>();

            // --- Add Logging Before Property Loop ---
            // McpLog.Info($"[GetComponentData] Starting property loop for {componentType.Name}...");
            // --- End Logging Before Property Loop ---

            // Use cached properties
            foreach (var propInfo in cachedData.SerializableProperties)
            {
                string propName = propInfo.Name;

                // --- Skip known obsolete/problematic Component shortcut properties ---
                bool skipProperty = false;
                if (propName == "rigidbody" || propName == "rigidbody2D" || propName == "camera" ||
                    propName == "light" || propName == "animation" || propName == "constantForce" ||
                    propName == "renderer" || propName == "audio" || propName == "networkView" ||
                    propName == "collider" || propName == "collider2D" || propName == "hingeJoint" ||
                    propName == "particleSystem" ||
                    // Also skip potentially problematic Matrix properties prone to cycles/errors
                    propName == "worldToLocalMatrix" || propName == "localToWorldMatrix")
                {
                    // McpLog.Info($"[GetComponentData] Explicitly skipping generic property: {propName}"); // Optional log
                    skipProperty = true;
                }
                // --- End Skip Generic Properties ---

                // --- Skip specific potentially problematic Camera properties ---
                if (componentType == typeof(Camera) &&
                    (propName == "pixelRect" ||
                     propName == "rect" ||
                     propName == "cullingMatrix" ||
                     propName == "useOcclusionCulling" ||
                     propName == "worldToCameraMatrix" ||
                     propName == "projectionMatrix" ||
                     propName == "nonJitteredProjectionMatrix" ||
                     propName == "previousViewProjectionMatrix" ||
                     propName == "cameraToWorldMatrix"))
                {
                    // McpLog.Info($"[GetComponentData] Explicitly skipping Camera property: {propName}");
                    skipProperty = true;
                }
                // --- End Skip Camera Properties ---

                // --- Skip specific potentially problematic Transform properties ---
                if (componentType == typeof(Transform) &&
                    (propName == "lossyScale" ||
                     propName == "rotation" ||
                     propName == "worldToLocalMatrix" ||
                     propName == "localToWorldMatrix"))
                {
                    // McpLog.Info($"[GetComponentData] Explicitly skipping Transform property: {propName}");
                    skipProperty = true;
                }
                // --- End Skip Transform Properties ---

                // Skip if flagged
                if (skipProperty)
                {
                    continue;
                }

                try
                {
                    // --- Add detailed logging --- 
                    // McpLog.Info($"[GetComponentData] Accessing: {componentType.Name}.{propName}");
                    // --- End detailed logging ---

                    // --- Special handling for material/mesh properties in edit mode ---
                    object value;
                    if (!Application.isPlaying && (propName == "material" || propName == "materials" || propName == "mesh"))
                    {
                        // In edit mode, use sharedMaterial/sharedMesh to avoid instantiation warnings
                        if ((propName == "material" || propName == "materials") && c is Renderer renderer)
                        {
                            if (propName == "material")
                                value = renderer.sharedMaterial;
                            else // materials
                                value = renderer.sharedMaterials;
                        }
                        else if (propName == "mesh" && c is MeshFilter meshFilter)
                        {
                            value = meshFilter.sharedMesh;
                        }
                        else
                        {
                            // Fallback to normal property access if type doesn't match
                            value = propInfo.GetValue(c);
                        }
                    }
                    else
                    {
                        value = propInfo.GetValue(c);
                    }
                    // --- End special handling ---

                    Type propType = propInfo.PropertyType;
                    AddSerializableValue(serializablePropertiesOutput, propName, propType, value);
                }
                catch (Exception)
                {
                    // McpLog.Warn($"Could not read property {propName} on {componentType.Name}");
                }
            }

            // --- Add Logging Before Field Loop ---
            // McpLog.Info($"[GetComponentData] Starting field loop for {componentType.Name}...");
            // --- End Logging Before Field Loop ---

            // Use cached fields
            foreach (var fieldInfo in cachedData.SerializableFields)
            {
                try
                {
                    // --- Add detailed logging for fields --- 
                    // McpLog.Info($"[GetComponentData] Accessing Field: {componentType.Name}.{fieldInfo.Name}");
                    // --- End detailed logging for fields ---
                    object value = fieldInfo.GetValue(c);
                    string fieldName = fieldInfo.Name;
                    Type fieldType = fieldInfo.FieldType;
                    AddSerializableValue(serializablePropertiesOutput, fieldName, fieldType, value);
                }
                catch (Exception)
                {
                    // McpLog.Warn($"Could not read field {fieldInfo.Name} on {componentType.Name}");
                }
            }
            // --- End Use cached metadata ---

            if (serializablePropertiesOutput.Count > 0)
            {
                data["properties"] = serializablePropertiesOutput;
            }

            return data;
        }

        /// <summary>
        /// Checks if a type is a Fusion networking type that should not be serialized.
        /// These types can crash Unity when their properties are accessed in edit mode.
        /// Also checks base types to catch user subclasses of networking components.
        /// </summary>
        private static bool IsFusionOrNetworkingType(Type type)
        {
            // Use the fast version - logging version kept for debugging if needed
            return IsFusionOrNetworkingTypeFast(type);
        }

        /// <summary>
        /// Fast version of IsFusionOrNetworkingType without logging - for use in tight loops.
        /// </summary>
        private static bool IsFusionOrNetworkingTypeFast(Type type)
        {
            if (type == null) return false;

            // Check the type itself and all base types
            Type currentType = type;
            while (currentType != null && currentType != typeof(MonoBehaviour) && currentType != typeof(Component) && currentType != typeof(object))
            {
                string ns = currentType.Namespace;
                if (!string.IsNullOrEmpty(ns))
                {
                    if (ns.StartsWith("Fusion", StringComparison.OrdinalIgnoreCase) ||
                        ns.Contains(".Fusion") ||
                        ns.StartsWith("Mirror", StringComparison.OrdinalIgnoreCase) ||
                        ns.StartsWith("Unity.Netcode", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                string typeName = currentType.Name;
                if (typeName == "NetworkRunner" || typeName == "NetworkObject" ||
                    typeName == "SimulationBehaviour" || typeName == "NetworkBehaviour" ||
                    typeName == "NetworkId" || typeName == "PlayerRef" ||
                    typeName == "TickTimer" || typeName == "NetworkPrefabRef" ||
                    typeName == "SceneRef")
                {
                    return true;
                }

                currentType = currentType.BaseType;
            }

            return false;
        }

        /// <summary>
        /// Attempts to serialize a Fusion/networking type to a meaningful representation.
        /// Returns null if serialization is not possible or unsafe.
        /// </summary>
        private static object TrySerializeFusionValue(object value, Type type)
        {
            if (value == null) return null;

            Type actualType = value.GetType();
            string typeName = actualType.Name;

            try
            {
                // Handle NetworkObject references - serialize like normal Component refs
                if (typeName == "NetworkObject" && value is Component networkObjectComponent)
                {
                    return new Dictionary<string, object>
                    {
                        { "name", networkObjectComponent.name },
                        { "instanceID", networkObjectComponent.GetInstanceID() }
                    };
                }

                // Handle NetworkBehaviour references - serialize like normal Component refs
                if (IsNetworkBehaviourType(actualType) && value is Component networkBehaviourComponent)
                {
                    return new Dictionary<string, object>
                    {
                        { "name", networkBehaviourComponent.name },
                        { "instanceID", networkBehaviourComponent.GetInstanceID() }
                    };
                }

                // Handle NetworkObjectGuid - try to get RawGuidValue
                if (typeName == "NetworkObjectGuid")
                {
                    var rawGuidField = actualType.GetField("RawGuidValue", BindingFlags.Public | BindingFlags.Instance);
                    if (rawGuidField != null)
                    {
                        try
                        {
                            object rawGuid = rawGuidField.GetValue(value);
                            if (rawGuid != null)
                            {
                                return rawGuid.ToString();
                            }
                        }
                        catch { }
                    }
                }

                // Handle NetworkObjectTypeId - get Kind and prefab ID if applicable
                if (typeName == "NetworkObjectTypeId")
                {
                    try
                    {
                        var result = new Dictionary<string, object>();

                        // Get Kind property (enum)
                        var kindProp = actualType.GetProperty("Kind", BindingFlags.Public | BindingFlags.Instance);
                        if (kindProp != null)
                        {
                            var kind = kindProp.GetValue(value);
                            result["kind"] = kind?.ToString();
                        }

                        // Get IsValid
                        var isValidProp = actualType.GetProperty("IsValid", BindingFlags.Public | BindingFlags.Instance);
                        if (isValidProp != null)
                        {
                            result["isValid"] = isValidProp.GetValue(value);
                        }

                        // Get IsPrefab
                        var isPrefabProp = actualType.GetProperty("IsPrefab", BindingFlags.Public | BindingFlags.Instance);
                        if (isPrefabProp != null)
                        {
                            bool isPrefab = (bool)isPrefabProp.GetValue(value);
                            result["isPrefab"] = isPrefab;

                            // If it's a prefab, try to get AsPrefabId
                            if (isPrefab)
                            {
                                var asPrefabIdProp = actualType.GetProperty("AsPrefabId", BindingFlags.Public | BindingFlags.Instance);
                                if (asPrefabIdProp != null)
                                {
                                    try
                                    {
                                        var prefabId = asPrefabIdProp.GetValue(value);
                                        result["prefabId"] = prefabId?.ToString();
                                    }
                                    catch { }
                                }
                            }
                        }

                        if (result.Count > 0)
                        {
                            return result;
                        }
                    }
                    catch { }
                }

                // Handle arrays of NetworkBehaviour or NetworkObject
                if (actualType.IsArray)
                {
                    Type elementType = actualType.GetElementType();
                    if (elementType != null && (IsNetworkBehaviourType(elementType) || elementType.Name == "NetworkObject"))
                    {
                        var array = value as Array;
                        if (array != null)
                        {
                            var result = new List<object>();
                            foreach (var item in array)
                            {
                                if (item == null)
                                {
                                    result.Add(null);
                                }
                                else if (item is Component comp)
                                {
                                    result.Add(new Dictionary<string, object>
                                    {
                                        { "name", comp.name },
                                        { "instanceID", comp.GetInstanceID() }
                                    });
                                }
                            }
                            return result;
                        }
                    }
                }

                // Handle Fusion value types with implicit conversion operators (NetworkBool, etc.)
                // Try to find op_Implicit that converts to a primitive type
                var implicitOps = actualType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "op_Implicit" && m.GetParameters().Length == 1
                           && m.GetParameters()[0].ParameterType == actualType
                           && (m.ReturnType.IsPrimitive || m.ReturnType == typeof(string)));

                foreach (var op in implicitOps)
                {
                    try
                    {
                        object converted = op.Invoke(null, new[] { value });
                        if (converted != null)
                        {
                            return converted;
                        }
                    }
                    catch { /* Conversion failed */ }
                }

                // Handle Fusion value types with .Value or .Raw properties
                // NetworkBool, NetworkString<T>, Tick, PlayerRef, NetworkId, etc.

                // Try .Value property first (NetworkBool, NetworkString<T>, etc.)
                var valueProp = actualType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (valueProp != null)
                {
                    try
                    {
                        object innerValue = valueProp.GetValue(value);
                        // If the inner value is a simple type, return it directly
                        if (innerValue == null) return null;
                        Type innerType = innerValue.GetType();
                        if (innerType.IsPrimitive || innerType == typeof(string))
                        {
                            return innerValue;
                        }
                    }
                    catch { /* Value property not accessible in edit mode */ }
                }

                // Try .Raw property (NetworkId, Tick, PlayerRef, etc.)
                var rawProp = actualType.GetProperty("Raw", BindingFlags.Public | BindingFlags.Instance);
                if (rawProp != null)
                {
                    try
                    {
                        object rawValue = rawProp.GetValue(value);
                        var result = new Dictionary<string, object>
                        {
                            { "raw", rawValue }
                        };

                        // For PlayerRef, also try to get PlayerId
                        if (typeName == "PlayerRef")
                        {
                            var playerIdProp = actualType.GetProperty("PlayerId", BindingFlags.Public | BindingFlags.Instance);
                            if (playerIdProp != null)
                            {
                                result["playerId"] = playerIdProp.GetValue(value);
                            }
                            var isValidProp = actualType.GetProperty("IsRealPlayer", BindingFlags.Public | BindingFlags.Instance);
                            if (isValidProp != null)
                            {
                                result["isRealPlayer"] = isValidProp.GetValue(value);
                            }
                        }

                        // For NetworkId, try to get IsValid
                        if (typeName == "NetworkId")
                        {
                            var isValidProp = actualType.GetProperty("IsValid", BindingFlags.Public | BindingFlags.Instance);
                            if (isValidProp != null)
                            {
                                result["isValid"] = isValidProp.GetValue(value);
                            }
                        }

                        return result;
                    }
                    catch { /* Raw property not accessible */ }
                }

                // For Tick type
                if (typeName == "Tick")
                {
                    var rawField = actualType.GetField("Raw", BindingFlags.Public | BindingFlags.Instance);
                    if (rawField != null)
                    {
                        try
                        {
                            return new Dictionary<string, object> { { "raw", rawField.GetValue(value) } };
                        }
                        catch { }
                    }
                }

                // For enums in Fusion namespace, return as integer (consistent with normal enum serialization)
                if (actualType.IsEnum)
                {
                    return Convert.ToInt32(value);
                }

            }
            catch (Exception e)
            {
                McpLog.Warn($"[TrySerializeFusionValue] Error serializing {typeName}: {e.Message}");
            }

            // Return null to indicate we couldn't serialize it meaningfully
            return null;
        }

        /// <summary>
        /// Safe version of AddSerializableValue that doesn't re-check Fusion types (caller already checked).
        /// </summary>
        private static void AddSerializableValueSafe(Dictionary<string, object> dict, string name, Type type, object value)
        {
            if (value == null)
            {
                dict[name] = null;
                return;
            }

            try
            {
                JToken token = CreateTokenFromValue(value, type);
                if (token != null)
                {
                    dict[name] = ConvertJTokenToPlainObject(token);
                }
            }
            catch (Exception e)
            {
                dict[name] = $"<serialization error: {e.Message}>";
            }
        }

        // Helper function to decide how to serialize different types
        private static void AddSerializableValue(Dictionary<string, object> dict, string name, Type type, object value)
        {
            if (value == null)
            {
                dict[name] = null;
                return;
            }

            // Get actual runtime type of the value
            Type actualType = value.GetType();

            // Try to serialize Fusion/networking types to meaningful representations
            if (IsFusionOrNetworkingTypeFast(type) || IsFusionOrNetworkingTypeFast(actualType))
            {
                // Try to extract useful information from the Fusion type
                object fusionSerialized = TrySerializeFusionValue(value, actualType);
                if (fusionSerialized != null)
                {
                    dict[name] = fusionSerialized;
                }
                // If we couldn't serialize the Fusion/networking type meaningfully, just skip it entirely
                return;
            }

            // Also check element type for arrays and generic collections
            Type elementType = null;
            if (actualType.IsArray)
            {
                elementType = actualType.GetElementType();
            }
            else if (actualType.IsGenericType)
            {
                var genericArgs = actualType.GetGenericArguments();
                if (genericArgs.Length > 0)
                {
                    elementType = genericArgs[0];
                }
            }

            // If element type is a Fusion/networking type we can't serialize meaningfully, just skip it entirely
            if (elementType != null && IsFusionOrNetworkingTypeFast(elementType))
            {
                return;
            }

            try
            {
                // Use the helper that employs our custom serializer settings
                JToken token = CreateTokenFromValue(value, type);
                if (token != null) // Check if serialization succeeded in the helper
                {
                    // Convert JToken back to a basic object structure for the dictionary
                    dict[name] = ConvertJTokenToPlainObject(token);
                }
                // If token is null, it means serialization failed and a warning was logged.
            }
            catch (Exception e)
            {
                // Catch potential errors during JToken conversion or addition to dictionary
                McpLog.Warn($"[AddSerializableValue] Error processing value for '{name}' (Type: {type.FullName}): {e.Message}. Skipping.");
            }
        }

        // Helper to convert JToken back to basic object structure
        private static object ConvertJTokenToPlainObject(JToken token)
        {
            if (token == null) return null;

            switch (token.Type)
            {
                case JTokenType.Object:
                    var objDict = new Dictionary<string, object>();
                    foreach (var prop in ((JObject)token).Properties())
                    {
                        objDict[prop.Name] = ConvertJTokenToPlainObject(prop.Value);
                    }
                    return objDict;

                case JTokenType.Array:
                    var list = new List<object>();
                    foreach (var item in (JArray)token)
                    {
                        list.Add(ConvertJTokenToPlainObject(item));
                    }
                    return list;

                case JTokenType.Integer:
                    return token.ToObject<long>(); // Use long for safety
                case JTokenType.Float:
                    return token.ToObject<double>(); // Use double for safety
                case JTokenType.String:
                    return token.ToObject<string>();
                case JTokenType.Boolean:
                    return token.ToObject<bool>();
                case JTokenType.Date:
                    return token.ToObject<DateTime>();
                case JTokenType.Guid:
                    return token.ToObject<Guid>();
                case JTokenType.Uri:
                    return token.ToObject<Uri>();
                case JTokenType.TimeSpan:
                    return token.ToObject<TimeSpan>();
                case JTokenType.Bytes:
                    return token.ToObject<byte[]>();
                case JTokenType.Null:
                    return null;
                case JTokenType.Undefined:
                    return null; // Treat undefined as null

                default:
                    // Fallback for simple value types not explicitly listed
                    if (token is JValue jValue && jValue.Value != null)
                    {
                        return jValue.Value;
                    }
                    // McpLog.Warn($"Unsupported JTokenType encountered: {token.Type}. Returning null.");
                    return null;
            }
        }

        // --- Define custom JsonSerializerSettings for OUTPUT ---
        private static readonly JsonSerializerSettings _outputSerializerSettings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter>
            {
                new Vector3Converter(),
                new Vector2Converter(),
                new QuaternionConverter(),
                new ColorConverter(),
                new RectConverter(),
                new BoundsConverter(),
                new Matrix4x4Converter(), // Fix #478: Safe Matrix4x4 serialization for Cinemachine
                new UnityEngineObjectConverter() // Handles serialization of references
            },
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            // ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() } // Example if needed
        };
        private static readonly JsonSerializer _outputSerializer = JsonSerializer.Create(_outputSerializerSettings);
        // --- End Define custom JsonSerializerSettings ---

        // Helper to create JToken using the output serializer
        private static JToken CreateTokenFromValue(object value, Type type)
        {
            if (value == null) return JValue.CreateNull();

            try
            {
                // Use the pre-configured OUTPUT serializer instance
                return JToken.FromObject(value, _outputSerializer);
            }
            catch (JsonSerializationException e)
            {
                McpLog.Warn($"[GameObjectSerializer] Newtonsoft.Json Error serializing value of type {type.FullName}: {e.Message}. Skipping property/field.");
                return null; // Indicate serialization failure
            }
            catch (Exception e) // Catch other unexpected errors
            {
                McpLog.Warn($"[GameObjectSerializer] Unexpected error serializing value of type {type.FullName}: {e}. Skipping property/field.");
                return null; // Indicate serialization failure
            }
        }
    }
}
