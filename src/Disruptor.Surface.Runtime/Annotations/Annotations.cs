namespace Disruptor.Surface.Annotations;

/// <summary>Marker for a class that participates in the generated model. Classes must be declared partial and may declare at most one optional <see cref="IdAttribute"/>-annotated property.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TableAttribute : Attribute { }

/// <summary>Marks a [Table] class as the root of an aggregate. Membership of the aggregate is computed by walking [Children] reachability from the root; entities reachable from two roots produce a CG011 error.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AggregateRootAttribute : Attribute { }

/// <summary>Marks a class as the user-side composition root. The generator emits an instance <c>Load{Root}Async(transport, rootId, ct)</c> method on this class for every [AggregateRoot] in the model. Class must be partial. Construction (transport wiring, caches, telemetry, …) is left entirely to the user — the library promises minimal intrusion. CG018 fires when more than one class is tagged.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CompositionRootAttribute : Attribute { }

/// <summary>Marks a property as the entity's unique identifier.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class IdAttribute : Attribute { }

/// <summary>Marks a property as a persisted data field. Property accessors only — <c>{ get; set; }</c>.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class PropertyAttribute : Attribute { }

/// <summary>Marks the parent-link property — <c>partial T Name { get; set; }</c>.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class ParentAttribute : Attribute { }

/// <summary>Marks a sync get-only collection property as the children accessor in a hierarchical relationship.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class ChildrenAttribute : Attribute { }

/// <summary>Marks a reference property — <c>partial T Name { get; }</c> for mandatory or <c>partial T? Name { get; set; }</c> for optional.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class ReferenceAttribute : Attribute { }

/// <summary>Pairs with <see cref="ReferenceAttribute"/> to mark the reference as an owned/compositional sidecar (e.g. <c>Details</c>). The aggregate loader inline-expands the referenced record into the same query (<c>field.*</c> projection) and hydrates it alongside the owner. Plain <c>[Reference]</c> without <c>[Inline]</c> stores only the id — the referenced record is treated as a foreign pointer that the caller resolves separately.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class InlineAttribute : Attribute { }

/// <summary>Reference delete behavior — block deletion of the referenced record while this reference still points at it. The default for every <see cref="ReferenceAttribute"/> when no other behavior is supplied.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class RejectAttribute : Attribute { }

/// <summary>Reference delete behavior — clear this reference field when the referenced record is deleted. Requires a nullable reference (<c>T?</c>); CG012 fires otherwise.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class UnsetAttribute : Attribute { }

/// <summary>Reference delete behavior — delete the referencing record when the referenced record is deleted. Cascade-only cycles (CG014) are rejected at compile time.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class CascadeAttribute : Attribute { }

/// <summary>Reference delete behavior — leave the referencing record unchanged when the referenced record is deleted. May produce a dangling reference. Use for external/historical references resolved outside this model.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class IgnoreAttribute : Attribute { }

/// <summary>Shared abstract base for every user-defined relation attribute (forward and inverse). Property-only — relations declare model shape and aren't a method-naming convention. The generator detects relation membership by walking up to this base.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public abstract class RelationAttribute : Attribute { }

/// <summary>Abstract base for forward-direction relation attributes. Derive directly: <c>public sealed class RestrictsAttribute : ForwardRelation;</c>. Inheritance from this base IS the discoverability signal — no marker attribute needed.</summary>
public abstract class ForwardRelation : RelationAttribute { }

/// <summary>Abstract base for inverse-direction relation attributes. The type parameter points at the forward kind this inverse mirrors. Derive directly: <c>public sealed class RestrictedByAttribute : InverseRelation&lt;RestrictsAttribute&gt;;</c>.</summary>
public abstract class InverseRelation<TForward> : RelationAttribute
    where TForward : ForwardRelation
{
    public Type ForwardKind => typeof(TForward);
}

/// <summary>Assembly-level override that pins the value type used for every generated <c>{Name}Id</c>. Omit to default to <c>Ulid</c>. Built-in support: <c>Ulid</c>, <c>Guid</c>, <c>string</c>.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class RecordIdValueAttribute<TValue> : Attribute { }
