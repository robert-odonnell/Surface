using Surface.Annotations;
using Surface.Sample.Relations;
using Surface.Runtime;


namespace Surface.Sample.Models;

[Table]
public partial class Constraint
{
    [Id] public partial ConstraintId Id { get; set; }

    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [Parent] public partial Design Design { get; set; }

    [Property] public partial string Description { get; set; }

    [Restricts] public partial IReadOnlyCollection<IEntity> Restrictions { get; }

    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
    
    public void Restricts(IRestrictedBy entity)
    {
        Session.Relate<Restricts>(this, entity);
    }
}