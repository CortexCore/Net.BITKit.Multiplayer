# Engine-neutral network objects

This layer extracts the existing Unity object roster/lifecycle implementation into the source Package. It does not replace NetRpc, component synchronization, or application authentication, and does not introduce prediction/interpolation.

## Ownership of responsibilities

- Core `NetworkObjectService`: Host-authoritative spawn/despawn and owner changes; resource/scene keys; object IDs; world generation and revisions; late-join roster; stale asynchronous completion rejection; initialization and component readiness
- Core `INetworkObjectAdapter`: engine instance creation or scene lookup, identity binding, presentation updates, release
- Existing `IEntitiesService` and `INetworkIdentity`: the single component registration path. An adapter supplies a `NetEntity` in its `NetworkObjectInstance`; the service registers/unregisters that entity. Engine identity components do not automatically discover all gameplay components
- Unity facade: existing GameObject/prefab entrypoints and Unity scene identity/resource loading
- Godot adapter: PackedScene instantiation, Node identity, scene lookup and main-thread access

DI assembles these pieces; it does not replace the engine integration. Host is a separate authority role, never an implicit local Client. OwnerPeerId is metadata for local control; assigning an owner does not permit Client state mutation or automatically authorize application input RPCs. Applications still authenticate peers and enforce their input policy.

## Initial state and readiness

An asynchronous object may load after its first unreliable component frame. Unknown ordinary component frames remain discardable. After binding and registering the supplied `NetEntity`, the Client requests a reliable complete component manifest from the Host, validates scope, entity, component identities/schema and current registration, and applies it before announcing `Spawned`.

`CaptureEntityStateAsync` returns owned bytes. `ApplyEntityState` requires the exact registered local `NetEntity`, so a stale continuation cannot apply its result to a replacement registration. A snapshot read does not consume a pending periodic update to other clients. Failed/incompatible snapshots do not report readiness. Every endpoint must provide the same component set for a network object; engine-only display components do not belong in that set.

`Initializing` is the pre-readiness hook. Prefer supplying the complete `NetEntity` from the adapter rather than manually registering another identity. `Spawned` means this object's initial state is ready, not that all objects in a world or all peers have acknowledged a barrier.

The Host derives `RequiresEntityState` from its actual bound lease, ignoring a spawn template's flag. When true, a Client that omits its entity/component integration fails readiness explicitly. Purely visual objects may omit entities on both endpoints.

Client component registration resets the component's receive-revision baseline while preserving its temporary value until the first Host snapshot. Thus local constructor/initialization writes cannot accidentally outrank revision zero from the Host. Subsequent duplicate snapshots remain suppressed; rebinding a reused component starts a fresh receive baseline. Host registration retains its existing state and revision.

## Lifecycle contract

Each world is one service lifetime. Entity IDs are monotonic and never reused within it, including failed spawn attempts. Reusing numeric IDs in another world requires a new service, a new world generation, and a new Runtime/transport scope. Disposing a service releases its instances and registration lease; it does not silently migrate existing connections into another world.

Updates are compared per object rather than against one global latest revision. Thus an update for object B cannot hide an earlier still-valid update for object A. Removal records reject late spawn packets, and late-join snapshot reconciliation respects concurrent newer updates. Asynchronous creation results are checked again before registration and readiness; cancelled/removed/disposed work releases its instance instead of resurrecting it. A failed or not-yet-loaded scene can be retried explicitly.

Adapters must implement thread marshalling for every engine operation, including continuations after asynchronous loading and snapshot requests. Core supports asynchronous operations but does not designate a Unity/Godot thread.

Adapters may implement `INetworkObjectDispatcher` to restore their engine thread for incoming lifecycle messages and after asynchronous loads, hooks and initial component-capture gate waits. Public synchronous owner/despawn/dispose entrypoints must be called on that engine thread (the Unity facade verifies this). Cancellation can finish the caller's await before an uncooperative asset loader returns; Core releases that late instance once available. Keep engine dispatch available until outstanding asset cleanup can finish.

Each load owns a distinct entity registration lease, even when reusing an authored scene object. A stale release must clean only that lease and must not clear/deactivate the raw engine object if it is already bound to a different nonzero EntityId. Both adapters implement this identity guard.

## Protocol and migration

The new built-in object protocol uses the existing NetRpc native typed receiver and request/reply pipeline. It is registered by the service; the Core assembly itself does not require a consumer's Unity IL postprocessor to rewrite its methods. Application RPCs retain their existing generated/woven paths.

This is not wire-compatible with the former Unity-only `UnityNetworkObjects` target. Deploy matched source Package and adapter versions to all peers and rebuild/regenerate application artifacts as needed. Do not mix an old Unity object service with a new Godot/Core service in one room. Normal NetRpc component frames remain on their existing transport; the new object readiness response is an owned reliable manifest.

See the Unity repository's migration notes for retained signatures and deliberate changes. See `Samples/NetworkObjectsGodot` for the real-engine adapter/demo, and `network-objects-validation.md` for commands, evidence and unverified boundaries.
