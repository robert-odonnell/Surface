# Commit Command Emission System Specification

## 1. Purpose

This document specifies a command-based write system for the source-generated model/persistence library.

The system does not use immediate CRUD operations as its write model. Domain mutations are recorded as commands, accumulated during a unit of work, compacted into a final persistence intent, and emitted to the database on commit.

The design supports:

- last-write-wins field updates,
- unset tracking,
- record creation,
- record deletion,
- create/delete/create lifecycle sequences,
- relation creation and removal,
- canonical relation direction,
- reference delete behavior,
- cascade delete expansion,
- generated protected domain APIs,
- compact commit emission,
- clear separation between write intent, read/query hydration, and database emission.

The system is intended for a SurrealDB-backed model, but the concepts are deliberately expressed as a general command emission model.

---

## 2. Design Principle

The system follows this rule:

```text
Domain code records intent.
The command packet preserves what happened.
The pending state tracks what currently matters.
The commit planner creates an ordered database plan.
The emitter writes that plan to the database.
```

Generated domain APIs do not directly persist changes. They record mutation intent into a command packet.

Queries and hydration remain separate from the write command system.

---

## 3. Terminology

### 3.1 Model Command

A **model command** is a single recorded mutation intent.

Examples:

```text
Create record
Delete record
Set field
Unset field
Relate records
Unrelate records
Upsert record
```

A command describes something the domain model asked to happen. It is not necessarily emitted directly to the database.

---

### 3.2 Command Log

The **command log** is the append-only chronological list of model commands recorded during a unit of work.

It answers:

```text
What happened, and in what order?
```

The command log is useful for:

- debugging,
- diagnostics,
- explaining generated persistence behavior,
- tests,
- future undo or audit behavior,
- preserving lifecycle ordering.

The command log should not be physically reordered for compaction.

---

### 3.3 Pending State

The **pending state** is the indexed, compactable representation of the current write intent.

It answers:

```text
What does this unit of work currently intend to persist?
```

The pending state is updated as commands are recorded. It is not append-only.

The pending state contains:

```text
Record pending states
Relation pending states
```

It is the source used by the commit planner.

---

### 3.4 Record Pending State

A **record pending state** tracks the accumulated mutation intent for one record id.

It includes:

- record id,
- table/type,
- whether the record existed at the start of the unit of work,
- lifecycle segments,
- current lifecycle segment,
- pending field sets,
- pending field unsets,
- final lifecycle state.

---

### 3.5 Lifecycle Segment

A **lifecycle segment** is a contiguous period of record existence intent inside the command packet.

A new segment begins when a record is created or recreated after deletion.

Lifecycle segments are required to handle sequences such as:

```text
Create X
Set X.Name = "A"
Delete X
Create X
Set X.Name = "B"
```

That sequence cannot always be safely reduced to only:

```text
Create X with Name = "B"
```

If the record existed before the unit of work, or if delete/recreate semantics matter, the commit plan may need to emit:

```text
DELETE X
CREATE X CONTENT { Name = "B" }
```

A lifecycle segment preserves that distinction.

---

### 3.6 Relation Pending State

A **relation pending state** tracks the accumulated mutation intent for one canonical relation key.

It includes:

- relation kind/table,
- canonical source record id,
- canonical target record id,
- whether the relation existed at the start of the unit of work,
- final relation state,
- optional pending relation payload changes.

---

### 3.7 Relation Key

A **relation key** uniquely identifies a relation record by canonical direction.

```text
RelationKey = relation kind + canonical source id + canonical target id
```

For example, if the canonical relation is:

```text
constraint -restricts-> user_story
```

then an inverse API call such as:

```text
user_story.AddRestrictedByConstraint(constraint)
```

still records the relation key as:

```text
restricts(constraint, user_story)
```

The command system must never create a separate inverse relation fact.

---

### 3.8 Reference

A **reference** is a non-owning pointer from one record to another record.

References are declared with `[Reference]`.

References are not structural ownership. Structural ownership is declared with `[Parent]`.

A reference may point within the same aggregate or across aggregate boundaries according to the model rules. Cross-aggregate references must be id-based.

---

### 3.9 Reference Delete Behavior

A **reference delete behavior** defines what happens to a referencing record when the referenced record is deleted.

Reference delete behavior is declared with marker attributes:

```csharp
[Reference, Reject]
public DecisionId DecisionId { get; private set; }

[Reference, Unset]
public DecisionId? DecisionId { get; private set; }

[Reference, Cascade]
public FindingId FindingId { get; private set; }

[Reference, Ignore]
public ExternalRecordId ExternalId { get; private set; }
```

If no delete behavior marker is supplied, `[Reject]` is assumed.

---

### 3.10 Commit Plan

A **commit plan** is the ordered list of database operations produced from the pending state during commit.

It answers:

```text
What exact database operations should be emitted, and in what order?
```

The commit plan is not the same thing as the command log.

The command log records what happened chronologically.

The commit plan records what should be persisted after compaction.

---

### 3.11 Emitter

The **emitter** executes a commit plan against the database.

For SurrealDB, the emitter is responsible for turning commit plan operations into SurrealQL or API calls.

---

## 4. System Overview

```text
Generated protected API
        |
        v
Model command
        |
        v
Command log --------------.
        |                  |
        v                  |
Pending state              |
        |                  |
        v                  |
Reference delete planner   |
        |                  |
        v                  |
Commit planner             |
        |                  |
        v                  |
Commit plan                |
        |                  |
        v                  |
Emitter -------------------'
        |
        v
Database
```

The command log is preserved for chronological inspection.

The pending state is maintained for compacted write intent.

Reference delete behavior is resolved during planning.

The commit plan is produced at commit time.

The emitter writes the plan.

---

## 5. Command Vocabulary

The core command vocabulary is deliberately small.

### 5.1 Create Record

```text
Create(record)
```

Declares that a record should exist.

Used when a new entity is created.

---

### 5.2 Upsert Record

```text
Upsert(record)
```

Declares that a record should exist, creating or updating it as needed.

This is useful when the caller does not care whether the record already exists.

`Upsert` must not erase required delete-before-create semantics when a record has been deleted and recreated during the same unit of work.

---

### 5.3 Delete Record

```text
Delete(record)
```

Declares that a record should be deleted.

If the record is later created again in the same command packet, a new lifecycle segment is created.

---

### 5.4 Set Field

```text
Set(record, field, value)
```

Declares that a field should be assigned a value.

Multiple sets to the same field in the same lifecycle segment are compacted using last-write-wins.

---

### 5.5 Unset Field

```text
Unset(record, field)
```

Declares that a field should be removed or set to absent/null according to the database mapping rules.

`Unset` cancels any pending set for the same field in the same lifecycle segment.

---

### 5.6 Relate Records

```text
Relate(relationKind, source, target, payload?)
```

Declares that a canonical relation should exist.

The source and target must be normalized to the canonical relation direction before the command reaches the pending state.

---

### 5.7 Unrelate Records

```text
Unrelate(relationKind, source, target)
```

Declares that a canonical relation should not exist.

The source and target must be normalized to the canonical relation direction before the command reaches the pending state.

If relation records have stable ids, this may be emitted as a relation record delete.

---

## 6. Command Recording Rules

When a mutation occurs, the system must perform two actions:

```text
1. Append the model command to the command log.
2. Apply the command to the pending state.
```

The command log preserves history.

The pending state maintains compacted intent.

The system should not wait until commit to perform all compaction. Simple field and relation compaction should occur as commands are recorded.

---

## 7. Record Pending State Rules

### 7.1 Field Set

When recording:

```text
Set(record, field, value)
```

the current lifecycle segment is updated:

```text
remove field from pending unsets
set pending field value
```

If the same field is set again, the previous pending value is replaced.

Example:

```text
Set Story.Heading = "A"
Set Story.Heading = "B"
Set Story.Heading = "C"
```

pending state:

```text
Story.Heading = "C"
```

---

### 7.2 Field Unset

When recording:

```text
Unset(record, field)
```

the current lifecycle segment is updated:

```text
remove field from pending sets
add field to pending unsets
```

Example:

```text
Set Story.Benefit = "Compliance"
Unset Story.Benefit
```

pending state:

```text
Story.Benefit is unset
```

---

### 7.3 Create

When recording:

```text
Create(record)
```

the record pending state is updated.

If the record is not currently deleted in the active lifecycle segment, the current segment is marked as created.

If the record was deleted in the active lifecycle segment, the current segment is closed and a new lifecycle segment is started.

This preserves create/delete/create semantics.

---

### 7.4 Upsert

When recording:

```text
Upsert(record)
```

the current lifecycle segment is marked as requiring existence.

An upsert may be emitted as either create/update/upsert depending on database strategy and record state.

An upsert must not remove a previous delete operation if the record was explicitly deleted and recreated in the same unit of work.

---

### 7.5 Delete

When recording:

```text
Delete(record)
```

the active lifecycle segment is marked as deleted.

Pending field sets and unsets in the deleted segment do not need to be emitted unless they are required before deletion for domain-specific reasons. The default rule is that deletion cancels pending field changes in that segment.

If the record is created again later, a new lifecycle segment is started.

Reference delete behavior is not fully resolved when the delete command is recorded. It is resolved during planning against the effective final pending state.

For [Parent] fields, default to cascade delete semantics (all childrend deleted) for now.

---

## 8. Lifecycle Segment Rules

Each record pending state has one or more lifecycle segments.

A lifecycle segment may contain:

```text
created/upserted flag
deleted flag
field sets
field unsets
```

### 8.1 Example: Create Then Set

Command log:

```text
Create story:1
Set story:1.heading = "Export audit logs"
Set story:1.summary = "Allows export of audit logs"
```

Pending lifecycle:

```text
segment 1:
  create story:1
  heading = "Export audit logs"
  summary = "Allows export of audit logs"
```

Possible commit operation:

```text
CREATE story:1 CONTENT {
  heading: "Export audit logs",
  summary: "Allows export of audit logs"
}
```

---

### 8.2 Example: Existing Record Updated

Assume `story:1` existed at the start of the unit of work.

Command log:

```text
Set story:1.heading = "A"
Set story:1.heading = "B"
Unset story:1.benefit
```

Pending lifecycle:

```text
segment 1:
  heading = "B"
  unset benefit
```

Possible commit operations:

```text
UPDATE story:1 SET heading = "B"
UPDATE story:1 UNSET benefit
```

---

### 8.3 Example: Create Then Delete

Assume `story:1` did not exist at the start of the unit of work.

Command log:

```text
Create story:1
Set story:1.heading = "A"
Delete story:1
```

Pending lifecycle:

```text
segment 1:
  created
  deleted
```

Possible commit operation:

```text
no database operation required
```

Because the record did not exist before and the final state is non-existence.

---

### 8.4 Example: Existing Record Delete

Assume `story:1` existed at the start of the unit of work.

Command log:

```text
Set story:1.heading = "A"
Delete story:1
```

Pending lifecycle:

```text
segment 1:
  deleted
```

Possible commit operation:

```text
DELETE story:1
```

The pending field set does not need to be emitted because the record is deleted.

---

### 8.5 Example: Existing Record Delete Then Create

Assume `story:1` existed at the start of the unit of work.

Command log:

```text
Delete story:1
Create story:1
Set story:1.heading = "B"
```

Pending lifecycle:

```text
segment 1:
  deleted

segment 2:
  created
  heading = "B"
```

Possible commit operations:

```text
DELETE story:1
CREATE story:1 CONTENT {
  heading: "B"
}
```

This preserves delete/recreate semantics.

---

### 8.6 Example: New Record Create Delete Create

Assume `story:1` did not exist at the start of the unit of work.

Command log:

```text
Create story:1
Set story:1.heading = "A"
Delete story:1
Create story:1
Set story:1.heading = "B"
```

Pending lifecycle:

```text
segment 1:
  created
  deleted

segment 2:
  created
  heading = "B"
```

Possible commit operation:

```text
CREATE story:1 CONTENT {
  heading: "B"
}
```

Because the first create/delete segment had no database-visible effect.

---

## 9. Relation Pending State Rules

All relation commands must be normalized to canonical direction before updating pending state.

### 9.1 Relate

When recording:

```text
Relate(kind, source, target, payload)
```

the relation pending state is updated:

```text
final state = related
payload changes = last-write-wins
```

If the same relation is related multiple times, the final state remains related.

---

### 9.2 Unrelate

When recording:

```text
Unrelate(kind, source, target)
```

the relation pending state is updated:

```text
final state = unrelated
clear pending payload changes
```

If the relation did not exist at the start of the unit of work and was only related earlier in the same unit of work, the final compacted state may require no database operation.

---

### 9.3 Relation Payload

If relation records carry payload fields, payload changes use the same set/unset rules as record fields.

Example:

```text
Relate story:1 depends_on story:2 { confidence = 0.7 }
Relate story:1 depends_on story:2 { confidence = 0.9 }
```

pending relation state:

```text
depends_on(story:1, story:2):
  related
  confidence = 0.9
```

---

### 9.4 Example: Relate Then Unrelate

Assume the relation did not exist at the start of the unit of work.

Command log:

```text
Relate story:1 depends_on story:2
Unrelate story:1 depends_on story:2
```

Pending relation state:

```text
final state = unrelated
existed at start = false
```

Possible commit operation:

```text
no database operation required
```

---

### 9.5 Example: Existing Relation Unrelate

Assume the relation existed at the start of the unit of work.

Command log:

```text
Unrelate story:1 depends_on story:2
```

Pending relation state:

```text
final state = unrelated
existed at start = true
```

Possible commit operation:

```text
DELETE relation depends_on(story:1, story:2)
```

---

### 9.6 Example: Unrelate Then Relate

Assume the relation existed at the start of the unit of work.

Command log:

```text
Unrelate story:1 depends_on story:2
Relate story:1 depends_on story:2
```

Pending relation state:

```text
final state = related
```

Possible commit operation:

```text
UPSERT/RELATE depends_on(story:1, story:2)
```

Depending on relation payload and database strategy, the emitter may produce no operation if the final relation state and payload match the original state.

---

## 10. Reference Delete Behavior

Reference delete behavior controls what happens when a record being deleted is referenced by other records.

Delete behavior applies to `[Reference]`, not to `[Parent]`.

Structural containment is handled by `[Parent]` / `[Children]`.

### 10.1 Attribute Syntax

Delete behavior is declared using marker attributes:

```csharp
[Reference, Reject]
public DecisionId DecisionId { get; private set; }

[Reference, Unset]
public DecisionId? DecisionId { get; private set; }

[Reference, Cascade]
public FindingId FindingId { get; private set; }

[Reference, Ignore]
public ExternalRecordId ExternalId { get; private set; }
```

If no delete behavior is declared, `[Reject]` is assumed.

```csharp
[Reference]
public DecisionId DecisionId { get; private set; }

// equivalent to:
[Reference, Reject]
public DecisionId DecisionId { get; private set; }
```

Only one delete behavior marker may be applied to a reference.

This is invalid:

```csharp
[Reference, Cascade, Unset]
public DecisionId DecisionId { get; private set; }
```

---

### 10.2 Reject

`[Reject]` means the referenced record cannot be deleted while effective references to it exist.

Example:

```text
story:1.decision = decision:9
Delete decision:9
```

Commit planning fails:

```text
Cannot delete decision:9.
Referenced by story:1 through UserStory.Decision.
```

`Reject` is the default because silent reference cleanup is dangerous.

---

### 10.3 Unset

`[Unset]` means the referencing field is unset when the referenced record is deleted.

Example:

```text
story:1.decision = decision:9
Delete decision:9
```

Planner records:

```text
Unset story:1.decision
```

Possible commit plan:

```text
UPDATE story:1 UNSET decision
DELETE decision:9
```

`Unset` requires nullable storage or a supported unsettable reference wrapper.

This is valid:

```csharp
[Reference, Unset]
public DecisionId? DecisionId { get; private set; }
```

This should produce a diagnostic:

```csharp
[Reference, Unset]
public DecisionId DecisionId { get; private set; }
```

because the reference cannot be unset safely.

---

### 10.4 Cascade

`[Cascade]` means the referencing record is deleted when the referenced record is deleted.

Example:

```text
finding:1.primary_statement = statement:7
Delete statement:7
```

Planner records:

```text
Delete finding:1
```

Cascade is powerful and should be explicit.

Cascade deletes are expanded during planning by adding generated `Delete` commands back into pending state.

Cascade is not recommended for ordinary cross-aggregate business references unless the referencing record truly has no meaning without the referenced record.

---

### 10.5 Ignore

`[Ignore]` means the referencing record is left unchanged when the referenced record is deleted.

Example:

```text
story:1.external_record = external:9
Delete external:9
```

Planner does nothing.

This may create a dangling reference.

`Ignore` should be explicit. It is suitable for:

- external records,
- historical references,
- audit/provenance references,
- references resolved outside the current model.

---

### 10.6 Default Behavior

The default behavior for `[Reference]` is always `[Reject]`.

The system should not infer `[Unset]` from nullable reference shape.

Nullable reference shape means the field can be absent. It does not automatically mean the system should clear it when the target is deleted.

---

## 11. Reference Delete Planning

Reference delete behavior is resolved during planning, not immediately when `Delete(record)` is recorded.

This is critical because the command packet may change the effective final reference state before commit.

Example:

Initial database state:

```text
story:1.decision = decision:9
```

Commands:

```text
Set story:1.decision = decision:10
Delete decision:9
```

The final effective state no longer references `decision:9`, so the delete should not be rejected.

### 11.1 Effective Incoming References

When a record is marked for deletion, the planner must determine effective incoming references by combining:

```text
database state
+ pending Set commands
+ pending Unset commands
+ pending Delete commands
+ pending Create/Recreate lifecycle state
```

The planner should only apply reference delete behavior to effective incoming references that still exist at commit time.

---

### 11.2 Delete Planning Algorithm

For each record pending deletion:

```text
1. Find effective incoming [Reference] members targeting the record.
2. For each incoming reference:
   - Reject: add a delete blocker.
   - Unset: record Unset(referencingRecord, referenceField).
   - Cascade: record Delete(referencingRecord).
   - Ignore: do nothing.
3. Apply generated Unset/Delete commands back into pending state.
4. Repeat until no new cascade/unset commands are produced.
5. If blockers exist, fail before emission.
6. Build the commit plan.
```

Generated commands from reference delete behavior must go through pending state so normal compaction rules still apply.

---

### 11.3 Reject Example

Initial state:

```text
story:1.decision = decision:9
```

Reference:

```csharp
[Reference]
public DecisionId DecisionId { get; private set; }
```

Commands:

```text
Delete decision:9
```

Commit planning fails:

```text
Cannot delete decision:9.
Referenced by story:1 through UserStory.DecisionId.
```

---

### 11.4 Pending Reassignment Avoids Reject

Initial state:

```text
story:1.decision = decision:9
```

Commands:

```text
Set story:1.decision = decision:10
Delete decision:9
```

Effective final state:

```text
story:1 no longer references decision:9
```

Commit may proceed.

Possible commit plan:

```text
UPDATE story:1 SET decision = decision:10
DELETE decision:9
```

---

### 11.5 Unset Example

Initial state:

```text
story:1.decision = decision:9
```

Reference:

```csharp
[Reference, Unset]
public DecisionId? DecisionId { get; private set; }
```

Commands:

```text
Delete decision:9
```

Planner records:

```text
Unset story:1.decision
```

Possible commit plan:

```text
UPDATE story:1 UNSET decision
DELETE decision:9
```

---

### 11.6 Cascade Example

Initial state:

```text
finding:1.primary_statement = statement:7
```

Reference:

```csharp
[Reference, Cascade]
public StatementId PrimaryStatementId { get; private set; }
```

Commands:

```text
Delete statement:7
```

Planner records:

```text
Delete finding:1
```

Possible commit plan:

```text
DELETE finding:1
DELETE statement:7
```

---

## 12. Cascade Cycle Rules

Cascade delete cycles are invalid by default.

The generator should build a static reference delete graph using `[Reference, Cascade]` edges.

Any cycle consisting only of cascade edges must be rejected at compile time.

Example invalid cycle:

```text
A --[Reference, Cascade]--> B
B --[Reference, Cascade]--> A
```

Diagnostic:

```text
Cascade delete cycle detected:
A -> B -> A.
Break the cycle by changing at least one reference to [Unset], [Reject], or [Ignore].
```

Longer invalid cycle:

```text
A --[Cascade]--> B
B --[Cascade]--> C
C --[Cascade]--> A
```

A cycle is allowed only if at least one edge breaks cascade propagation through:

```text
[Unset]
[Reject]
[Ignore]
```

Mixed cycles that contain cascade edges but are broken by non-cascade behavior may be allowed, but the generator may issue a warning because the deletion semantics are non-trivial.

---

## 13. Parent Deletion

`[Parent]` is structural ownership, not a normal reference.

Deleting a parent means deleting structurally contained children.

For v1, the default rule is:

```text
Parent deletion cascades to children.
```

This is containment cascade, not `[Reference, Cascade]`.

The planner should expand parent deletion by recording `Delete(child)` commands for effective direct children and repeating recursively.

Reference delete behavior does not apply to `[Parent]`.

If future support is needed, parent delete behavior should use a separate parent-specific policy, not `[Reference]` delete behavior.

---

## 14. Generated API Interaction

Generated protected APIs record commands. They do not persist directly.

Example domain relation:

```text
user_story -depends_on-> user_story
```

Generated protected API:

```csharp
protected void AddDependsOnUserStory(UserStory target);
protected bool RemoveDependsOnUserStory(UserStory target);
protected IReadOnlyList<UserStory> ListDependsOnUserStory();
```

Write methods record commands:

```text
AddDependsOnUserStory(target)
  -> Relate(depends_on, this.Id, target.Id)

RemoveDependsOnUserStory(target)
  -> Unrelate(depends_on, this.Id, target.Id)
```

If the generated API is inverse:

```csharp
protected void AddDependedOnByUserStory(UserStory source);
```

it still records the canonical relation:

```text
Relate(depends_on, source.Id, this.Id)
```

The inverse API must never record an inverse relation table.

---

## 15. Command Log Ordering

The command log must remain chronological.

The system should not physically move record commands to make commands for the same record adjacent.

Instead, the system should maintain indexes inside pending state.

This allows the system to have both:

```text
chronological history
compactable current intent
```

Physical reordering of the command log is discouraged because:

- cross-record ordering may matter,
- relation operations may depend on endpoint lifecycle,
- debugging becomes harder,
- explanation/audit becomes misleading.

Grouping by record or relation belongs in pending state and commit planning, not in the command log.

---

## 16. Commit Planning

The commit planner converts pending state into a commit plan.

Before creating the final commit plan, the planner resolves:

```text
parent deletion expansion
reference delete behavior
cascade delete expansion
reject blockers
```

The commit plan must be ordered to preserve lifecycle semantics and relation correctness.

The planner should generally follow these phases:

```text
1. Expand parent containment deletes.
2. Resolve reference delete behavior.
3. Expand cascade reference deletes.
4. Fail if reject blockers remain.
5. Emit deletes required before recreate.
6. Emit creates and upserts.
7. Emit field sets and unsets for surviving records.
8. Emit relation removals.
9. Emit relation additions/upserts.
10. Emit final record deletes.
```

The exact phase ordering may be adjusted for database-specific behavior, but the planner must ensure:

- parent deletion deletes contained children,
- reference delete behavior is applied to effective final state,
- records exist before relations to them are created,
- delete/recreate sequences are preserved,
- relations are removed when required,
- final deleted records are deleted,
- field updates to records that are deleted are not emitted unnecessarily.

---

## 17. Commit Plan Operations

A commit plan contains database-facing operations.

Possible operation types:

```text
CreateRecord
UpsertRecord
UpdateRecordFields
UnsetRecordFields
DeleteRecord
CreateRelation
UpsertRelation
UpdateRelationPayload
DeleteRelation
```

Commit plan operations are not the same as model commands.

Model commands are domain mutation intent.

Commit plan operations are database write instructions after compaction.

---

## 18. Emission Rules

The emitter takes a commit plan and executes it.

For SurrealDB, the emitter may choose between:

- individual statements,
- batched statements,
- transaction-style grouped statements,
- `CREATE`,
- `UPDATE`,
- `UPSERT`,
- `DELETE`,
- `RELATE`.

The emitter owns database-specific syntax.

The command packet and pending state should remain database-shape aware only where required by canonical ids, table names, and relation kinds.

---

## 19. Read Side Separation

The command packet write model does not replace query/hydration.

Reads are handled by a separate model reader or query provider.

Read responsibilities include:

- load record by id,
- hydrate table records,
- resolve parent/reference members,
- compute direct children,
- list direct relation projections,
- execute custom SurrealQL graph queries,
- hydrate projections.

The write command system should not become a general query abstraction.

---

## 20. Error Handling and Validation

The system should validate commands as early as practical.

Examples:

```text
Cannot set a field on an unknown record unless upsert/create semantics allow it.
Cannot relate records with an invalid relation kind.
Cannot relate endpoint types outside the relation definition.
Cannot create inverse relation facts.
Cannot use a deleted record as a relation endpoint unless it is recreated before commit.
Cannot emit relation creation before endpoint records exist.
Cannot delete a referenced record when effective incoming [Reject] references remain.
Cannot unset a non-nullable reference.
Cannot compile a cascade-only reference cycle.
```

Some validation is compile-time via the source generator.

Some validation is runtime because it depends on unit-of-work state.

---

## 21. Generator Diagnostics

The generator should emit diagnostics for invalid reference delete declarations.

Examples:

```text
SURF-R001:
[Reference, Unset] requires nullable storage or a supported unsettable reference wrapper.

SURF-R002:
A [Reference] member cannot declare multiple delete behaviors.

SURF-R003:
[Reference, Cascade] creates a cascade delete cycle.

SURF-R004:
[Parent] cannot use [Reject], [Unset], [Cascade], or [Ignore].
Parent deletion uses structural containment rules.

SURF-R005:
Cross-aggregate typed object reference is invalid. Use id/ref-based [Reference].

SURF-R006:
[Reference, Ignore] to a known table creates possible dangling references.
This may be allowed, but should be explicit.
```

---

## 22. Diagnostics and Debugging

The command packet should support diagnostic inspection.

Useful debug views:

```text
Command log
Pending record states
Pending relation states
Lifecycle segments
Reference delete decisions
Cascade expansion trace
Reject blockers
Compacted commit plan
SurrealQL emission preview
```

This allows the developer to answer:

```text
Why did this field update disappear?
Why did this delete remain?
Why was this relation emitted?
Why did this create become an upsert?
Why did this create/delete/create sequence emit two operations?
Why did this reference block deletion?
Why did this delete cascade?
Why was this reference unset?
```

The command system should be transparent. Hidden compaction is a bug farm.

---

## 23. Recommended Runtime Shape

Conceptual interfaces:

```csharp
public interface IModelCommandSink
{
    void Record(ModelCommand command);
}

public interface IModelReader
{
    // read/hydration/query operations
}

public interface ICommitPlanner
{
    CommitPlan Build(ModelCommandPacket packet);
}

public interface ICommitEmitter
{
    ValueTask EmitAsync(CommitPlan plan, CancellationToken cancellationToken = default);
}
```

Core object:

```csharp
public sealed class ModelCommandPacket : IModelCommandSink
{
    public IReadOnlyList<ModelCommand> CommandLog { get; }

    public void Record(ModelCommand command);

    public CommitPlan BuildCommitPlan();
}
```

Generated protected APIs depend on `IModelCommandSink`.

Queries depend on `IModelReader`.

Commit depends on `ICommitPlanner` and `ICommitEmitter`.

---

## 24. Non-Goals

The system is not:

- an immediate CRUD abstraction,
- a general-purpose query language,
- a LINQ provider,
- an event-sourcing framework,
- a public audit log by default,
- a replacement for SurrealQL,
- a business workflow engine.

It is a compact command emission system for model persistence.

---

## 25. Core Invariants

The system must preserve these invariants:

```text
The command log is chronological.
The pending state is compactable.
The commit plan is ordered for database correctness.
Field writes are last-write-wins within a lifecycle segment.
Unset cancels pending set for the same field.
Delete cancels pending field writes in the deleted lifecycle segment.
Create after delete starts a new lifecycle segment.
Relations are normalized to canonical direction.
Inverse APIs never create inverse relation facts.
Relation writes are compacted by canonical relation key.
Endpoint records must exist before relation creation is emitted.
[Reference] defaults to [Reject].
Reference delete behavior is resolved against effective final pending state.
[Unset] references must be nullable/unsettable.
[Cascade] expands into Delete commands during planning.
Cascade-only reference cycles are invalid.
[Parent] deletion is structural containment deletion, not reference deletion.
Generated mutation APIs record commands; they do not directly persist.
Queries are separate from command emission.
```

---

## 26. Short Summary

The write model should be:

```text
Append command
Update pending state
Preserve lifecycle segments
Normalize relation direction
Resolve parent/reference delete behavior during planning
Compact at commit
Emit ordered database operations
```

The system should not reorder the command log.

It should compact intent as commands arrive and build the final database plan only at commit.

The best guiding phrase is:

```text
Compact intent on the fly.
Preserve history append-only.
Resolve delete behavior before emission.
Emit a planned database command packet at commit.
```
