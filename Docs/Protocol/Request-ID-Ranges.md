# MMO Client -> GameServer Request ID Allocation

**Status:** Canonical allocation policy  
**Request ID type:** `ushort` (`0-65535`)  
**Scope:** Client-to-GameServer request operations only

## Core rule

**Request IDs identify protocol operations, not game content.**

A game can have hundreds of thousands of items, quests, effects, social actions, animations, recipes, vehicles, furniture pieces, or NPC definitions without consuming one request ID per definition.

Examples:

- `Equip` is one request operation; the payload identifies the item instance and equipment slot.
- `BeginInteraction` can eventually be one request operation; the payload identifies an `interactionDefinitionId` such as a hug, dance, romance action, adult action, supernatural action, or mod-defined action.
- `Craft` can be one operation; the payload identifies the recipe.
- `AcceptQuest` can be one operation; the payload identifies the quest.

This keeps the wire protocol compact and lets content grow independently of the network opcode table.

## Canonical ranges

| Range | Size | Owner / Purpose | Notes |
|---|---:|---|---|
| `0000-0099` | 100 | Core networking / session | LiteNetLib game admission/readiness and future core session operations. |
| `0100-0199` | 100 | Character / account session | Character list, admission authentication, enter/create character, future character-session operations. |
| `0200-0299` | 100 | Inventory / equipment / world items | Item count does not consume request IDs; item definitions/instances travel in payloads. |
| `0300-0349` | 50 | Character vitals / resources | Health, mana, stamina, blood, hunger, thirst, energy, etc. **Not** logs/herbs/rocks/material definitions. |
| `0350-0399` | 50 | Progression / stats / talents | Reserved for character progression systems. |
| `0400-0449` | 50 | Status / effects | Effect definitions do not consume request IDs. |
| `0450-0499` | 50 | System expansion reserve | Keep free for future first-party protocol pressure near existing low ranges. |
| `0500-0599` | 100 | Combat / abilities / character actions | Existing combat/ability operations live here. Legacy interaction IDs are grandfathered here; new interaction protocol work goes to `1300-1399`. |
| `0600-0649` | 50 | Staff / administration | Staff status, visibility, observation/spectate, future admin operations. |
| `0650-0699` | 50 | Instance / matchmaking / world session | Future world-session and matchmaking operations. |
| `0700-0749` | 50 | Party / friends | Party membership and friend/social-network operations, not Sims-scale interaction content. |
| `0750-0849` | 100 | Guild / community | Guild membership, ranks, permissions, guild bank/community features, recruitment, etc. |
| `0850-0949` | 100 | Economy / trade / vendor / bank / mail / auction | Deliberately larger because economy protocols tend to accumulate distinct operations. |
| `0950-0999` | 50 | Quests / journal | Quest definitions use content IDs; request IDs represent generic verbs such as accept/abandon/turn-in. |
| `1000-1049` | 50 | Crafting / gathering / orders | Recipe/resource-node definitions are content, not request IDs. |
| `1050-1099` | 50 | Vehicles / transport | Vehicle definitions are content IDs; requests are generic enter/exit/seat/control/service operations. |
| `1100-1249` | 150 | Housing / property / neighborhood | Intentionally large for ownership, permissions, rooms, furniture, placement, storage, leases, venues, neighborhoods, etc. |
| `1250-1299` | 50 | NPC / world gameplay | Generic world/NPC protocol operations that do not belong to the deep interaction session protocol. |
| `1300-1399` | 100 | **Interaction / social / adult interaction protocol** | One of the deepest systems in the game. Operations remain generic; actual social/romance/adult/mod actions are definition-driven. |
| `1400-1449` | 50 | Relationships / household / family | Persistent relationships and household/family state, separate from temporary interaction sessions. |
| `1450-1499` | 50 | Pets / companions | Companion ownership/control/state requests. |
| `1500-1549` | 50 | Activities / venues / events | Venue/activity/event participation operations. |
| `1550-1999` | 450 | Future first-party systems | Unassigned first-party reserve. Do not allocate casually. |
| `2000-4095` | 2096 | First-party expansion reserve | Long-term first-party protocol growth. |
| `4096-8191` | 4096 | Extension / plugin API | Controlled extension/plugin allocations. Allocation must be registered and collision-checked. |
| `8192-16383` | 8192 | Script / mod reserve | Future sandboxed script/mod protocol extensions. Content mods should still prefer definition-driven generic operations. |
| `16384-65535` | 49152 | Future reserved | Leave untouched until an explicit architecture decision assigns part of it. |

## Current grandfathered request IDs

Existing working IDs are **not renumbered** just to fit a prettier table. They are stable protocol history.

| ID | Request | Status | Range |
|---:|---|---|---|
| `0` | Enter Game | Implemented | Core |
| `1` | Client Ready | Implemented | Core |
| `2` | Client Not Ready | Implemented | Core |
| `100` | Character List | Implemented | Character / account |
| `101` | Enter Character | Implemented | Character / account |
| `102` | Authenticate Admission | Implemented | Character / account |
| `103` | Legacy Create Account | **Reserved / retired** | Character / account |
| `104` | Create Character | Implemented | Character / account |
| `200` | Item Snapshot | Implemented | Inventory / equipment / world items |
| `201` | Move Inventory | Implemented | Inventory / equipment / world items |
| `202` | Equip | Implemented | Inventory / equipment / world items |
| `203` | Unequip | Implemented | Inventory / equipment / world items |
| `204` | Use Item | Implemented | Inventory / equipment / world items |
| `205` | Drop Item | Implemented | Inventory / equipment / world items |
| `220` | World Item Snapshot | Implemented | Inventory / equipment / world items |
| `221` | Loot World Item | Implemented | Inventory / equipment / world items |
| `300` | Character Resource Snapshot | Implemented | Character vitals / resources |
| `400` | Status Effect Snapshot | Implemented | Status / effects |
| `500` | Basic Attack | Implemented | Combat / character actions |
| `501` | Begin Ability | Implemented | Combat / character actions |
| `502` | Cancel Ability | Implemented | Combat / character actions |
| `503` | Interaction | Implemented, **grandfathered** | Combat / character actions |
| `504` | Respawn | Implemented | Combat / character actions |
| `505` | Interaction Menu | Implemented, **grandfathered** | Combat / character actions |
| `506` | Context Interaction | Implemented, **grandfathered** | Combat / character actions |
| `600` | Staff Status | Implemented | Staff / administration |
| `601` | Set Staff Visibility | Implemented | Staff / administration |
| `602` | Spectate | Implemented | Staff / administration |
| `603` | Stop Spectate | Implemented | Staff / administration |

### Legacy interaction IDs

`503`, `505`, and `506` remain valid for compatibility. Do not renumber them.

However, **new deep interaction protocol operations must be allocated from `1300-1399`**, not appended to the `500` block.

The `1300-1399` range is for protocol verbs such as, eventually:

- begin interaction;
- accept / decline;
- cancel;
- join / leave;
- choose option / branch;
- participant change request / response;
- stage transition;
- position/anchor change request / response;
- interaction details/menu queries where a dedicated operation is justified.

It is **not** one ID per action. Thousands of interaction definitions can share these operations.

Example payload concept:

```text
BeginInteraction
    targetActor
    interactionDefinitionId
    context
```

Then content can define actions such as friendly, hostile, romance, adult, supernatural, venue-specific, or modded interactions without allocating a new request ID for each action.

## Separate ID namespaces

Do not mix these concepts.

### Request IDs

Client asks the GameServer to perform an operation.

Examples: equip, begin ability, accept party invite, begin interaction, place furniture.

### Server message/event IDs

Server tells clients that an asynchronous event/state change occurred.

Examples: damage result, equipment changed, status delta, interaction started, party changed.

These are a separate namespace from request IDs. Existing top-level LiteNetLib message IDs such as item/status deltas are **not** allocated from the table above.

### Content definition IDs

Identify authored gameplay content:

```text
item.armor.leather_chest
ability.vampire_bite
effect.poison_minor
interaction.social.hug
interaction.romance.kiss
interaction.adult.some_action
recipe.iron_sword
quest.main.0042
vehicle.sedan.03
furniture.sofa.modern.02
```

Content definition IDs do not consume request IDs.

### Runtime instance IDs

Identify an actual live/persisted instance:

- `characterId`
- `itemInstanceId`
- `objectId + generation`
- future `interactionSessionId`
- future `vehicleInstanceId`
- future `propertyId`

Runtime IDs also do not consume request IDs.

## Rules for another developer adding a request

1. **Choose the subsystem first.** Do not pick a convenient random number.
2. Allocate an unused ID inside that subsystem's range.
3. Never reuse a retired/reserved historical ID unless the protocol owner explicitly releases it.
4. Add the request constant to the appropriate request-types class.
5. Add/update the request serialization contract.
6. Add the same protocol source to the standalone GameServer protocol snapshot when applicable.
7. Update the current-allocation table in this document.
8. Update `RequestIdRangesTests` so duplicate/range validation covers the new ID.
9. Add the server handler/registration. Until implemented, a reserved request must remain a no-op/Unimplemented path rather than being interpreted as another operation.
10. Do not allocate one request ID per item/effect/quest/recipe/social action/etc. Use a definition ID in the payload.

## Extension/plugin/mod policy

- `0-4095`: first-party ownership.
- `4096-8191`: controlled extension/plugin API.
- `8192-16383`: future script/mod protocol reserve.
- `16384+`: do not allocate without an architecture decision.

A plugin/mod that only adds content should usually need **zero new request IDs**. It should register new definitions behind existing generic server operations.

## Code authority

The canonical range declaration is:

```text
Assets/Player/Networking/RequestIdRanges.cs
```

The standalone server snapshot must contain an identical copy at:

```text
Server/Source/GameServer/Protocol/Player.Networking/RequestIdRanges.cs
```

The Unity Editor contract test is:

```text
Assets/Testing/Player/Tests/Editor/RequestIdRangesTests.cs
```

The test verifies:

- the full `ushort` namespace is covered without gaps or overlaps;
- all currently allocated request IDs are unique;
- existing IDs remain inside their grandfathered subsystem range.

## Future server dispatch architecture

The request-ID range policy is independent of how requests are dispatched.

The current large switch can continue working while this allocation policy is introduced. The recommended handoff architecture is still a `ServerRequestRegistry` where each subsystem registers its request handlers and startup fails on duplicate registrations.

The range policy should remain valid when that registry is introduced.
