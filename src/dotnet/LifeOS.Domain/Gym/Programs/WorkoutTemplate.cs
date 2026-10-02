namespace LifeOS.Domain.Gym.Programs;

// One workout (session) of a program, e.g. "Back / Triceps / Legs": an ordered list of blocks.
public sealed class WorkoutTemplate
{
    private readonly List<WorkoutBlock> _blocks = [];

    private WorkoutTemplate(Guid id, Guid workoutProgramId, Guid userId, string name, int position)
    {
        Id = id;
        WorkoutProgramId = workoutProgramId;
        UserId = userId;
        Name = name;
        Position = position;
    }

    public Guid Id { get; }

    public Guid WorkoutProgramId { get; }

    // The program's owner; the database's composite keys use it as an ownership backstop.
    public Guid UserId { get; }

    public string Name { get; private set; }

    // 1-based order within the program.
    public int Position { get; private set; }

    public IReadOnlyList<WorkoutBlock> Blocks => _blocks.OrderBy(block => block.Position).ToList();

    internal static WorkoutTemplate Create(WorkoutProgram program, string name, int position) =>
        new(Guid.CreateVersion7(), program.Id, program.UserId, WorkoutProgram.NormalizeName(name), position);

    public void Rename(string name)
    {
        Name = WorkoutProgram.NormalizeName(name);
    }

    public WorkoutBlock? FindBlock(Guid blockId) =>
        _blocks.SingleOrDefault(block => block.Id == blockId);

    // Appended after the last block. A Single block takes exactly one exercise prescription, a
    // Superset exactly two (A then B).
    public WorkoutBlock AddBlock(WorkoutBlockKind kind, int? restSeconds, IReadOnlyList<ExercisePrescription> exercises)
    {
        var block = WorkoutBlock.Create(this, _blocks.Count + 1, kind, restSeconds, exercises);
        _blocks.Add(block);

        return block;
    }

    // Removes the block with its prescriptions; the remaining blocks close the gap.
    public bool RemoveBlock(Guid blockId)
    {
        var block = FindBlock(blockId);

        if (block is null)
        {
            return false;
        }

        _blocks.Remove(block);
        Positions.Renumber(Blocks, (item, position) => item.MoveTo(position));

        return true;
    }

    // orderedBlockIds must list every block of the workout exactly once, in the new order.
    public void ReorderBlocks(IReadOnlyList<Guid> orderedBlockIds)
    {
        var ordered = Positions.Arrange(_blocks, block => block.Id, orderedBlockIds, "blockIds", "block");
        Positions.Renumber(ordered, (item, position) => item.MoveTo(position));
    }

    internal void MoveTo(int position)
    {
        Position = position;
    }
}
