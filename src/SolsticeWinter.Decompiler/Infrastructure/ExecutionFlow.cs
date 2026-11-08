using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace SolsticeWinter.Decompiler.Infrastructure;

public enum BlockType
{
    Unknown,
    Jump,
    BranchEnd,
    BranchEndIfNot,
    LatentAction,
    Return,
    JumpIfNot,
    JumpToEntryPoint,
}

/// <summary>
/// 一段直线代码。移植自参考项目 <c>InstrBlock</c>（MIT）。
/// 与参考实现的差异：<see cref="Start"/>/<see cref="End"/> 存的是**字节偏移**（不是下标），
/// 因为 UAssetAPI 没有 <c>StatementIndex</c>，偏移由 <see cref="StatementMap"/> 提供。
/// </summary>
public sealed class InstrBlock
{
    public List<KismetExpression> Instructions { get; } = [];
    public List<uint> Offsets { get; } = [];
    public List<BlockJump> Jumps { get; } = [];
    public List<Sequence> Sequences { get; } = [];

    public BlockType Type { get; set; }

    /// <summary>首条指令的字节偏移。</summary>
    public uint Start { get; private set; }

    /// <summary>末条指令的字节偏移。</summary>
    public uint End { get; private set; }

    public void Add(KismetExpression expression, uint offset)
    {
        if (Instructions.Count == 0) Start = offset;
        End = offset;
        Instructions.Add(expression);
        Offsets.Add(offset);
    }

    public override string ToString() => $"[{Start}..{End}] {Type}";

    public bool IsInside(uint offset) => offset >= Start && offset <= End;

    public int GetIndex(uint offset) => Offsets.IndexOf(offset);

    public Sequence GetSequence(int localIndex) =>
        Sequences.Find(s => s.SourceIndex <= localIndex && s.EndIndex >= localIndex)
        ?? throw new InvalidOperationException($"Sequence with index {localIndex} not found");
}

/// <summary>一条跳转：从 <see cref="Source"/> 的末条指令跳到 <see cref="Destination"/> 的第 <see cref="StartIndex"/> 条。</summary>
public sealed class BlockJump
{
    public BlockJump(InstrBlock source, InstrBlock destination)
    {
        Source = source;
        Destination = destination;
        SourceIndex = source.Instructions.Count - 1;
    }

    public BlockJump(InstrBlock source, InstrBlock destination, uint offset) : this(source, destination)
    {
        StartIndex = destination.GetIndex(offset);
    }

    public InstrBlock Source { get; }
    public InstrBlock Destination { get; }
    public int SourceIndex { get; set; }
    public int StartIndex { get; private set; }

    public override string ToString() => $"({Source}) -> ({Destination}) at {StartIndex}";
}

/// <summary>
/// 一段「PushExecutionFlow 序列」：连续 N 个 push + 一次落到顺序下一句。
/// 对应蓝图里的 Sequence 节点（多 exec 出脚）。
/// </summary>
public sealed class Sequence(List<BlockJump> points, int start, int end)
{
    public List<BlockJump> Points { get; } = points;
    public int SourceIndex { get; } = start;
    public int EndIndex { get; } = end;

    public bool HaveThisBranchStart(InstrBlock block, int index) =>
        Points.Exists(p => p.Destination == block && p.StartIndex == index);

    public List<BlockJump> GetPoints(int localIndex)
    {
        var start = localIndex - SourceIndex;
        return Points.GetRange(start, Points.Count - start);
    }
}

/// <summary>
/// 把线性字节码切成「基本块 + 跳转 + 序列」。移植自参考项目 <c>ExecutionFlow</c>（MIT）。
///
/// <para>
/// 切块规则：遇到 <c>JumpIfNot</c> / <c>Jump</c> / <c>PopExecutionFlow</c> /
/// <c>PopExecutionFlowIfNot</c> / <c>EndOfScript</c> / <c>ComputedJump</c> 就断块。
/// </para>
/// </summary>
public sealed class ExecutionFlow
{
    public List<InstrBlock> Blocks { get; } = [];
    public List<uint> EntryPoints { get; } = [];

    private readonly Dictionary<uint, BlockJump> _entryPointJumps = [];

    public void DecompileFlow(IEnumerable<KismetExpression> script, IReadOnlyDictionary<KismetExpression, uint> offsets)
    {
        FindBlocks(script, offsets);
        FindSequences();
        FindBasicJumps();
        ResolveEntryPointJumps();
    }

    private void FindBlocks(IEnumerable<KismetExpression> script, IReadOnlyDictionary<KismetExpression, uint> offsets)
    {
        var block = new InstrBlock();
        foreach (var expression in script)
        {
            if (!offsets.TryGetValue(expression, out var offset))
                throw new InvalidOperationException($"表达式 {expression.GetType().Name} 没有偏移映射");

            block.Add(expression, offset);

            BlockType? type = expression switch
            {
                EX_JumpIfNot => BlockType.JumpIfNot,
                EX_Jump => BlockType.Jump,
                EX_PopExecutionFlow => BlockType.BranchEnd,
                EX_PopExecutionFlowIfNot => BlockType.BranchEndIfNot,
                EX_EndOfScript => BlockType.Return,
                EX_ComputedJump => BlockType.JumpToEntryPoint,
                _ => null,
            };

            if (type is null) continue;

            block.Type = type.Value;
            Blocks.Add(block);
            block = new InstrBlock();
        }

        if (block.Instructions.Count > 0) Blocks.Add(block);
    }

    /// <summary>连续 <c>PushExecutionFlow</c> + 落到顺序下一句 = 一个 Sequence。</summary>
    private void FindSequences()
    {
        foreach (var b in Blocks)
        {
            for (var i = 0; i < b.Instructions.Count - 1; i++)
            {
                var points = new List<BlockJump>();
                var count = 0;

                while (i + count < b.Instructions.Count && b.Instructions[i + count] is EX_PushExecutionFlow push)
                {
                    var jump = FindBlockJump(push.PushingAddress, b);
                    jump.SourceIndex = i;
                    points.Add(jump);
                    count++;
                }

                if (count == 0) continue;

                if (i + count >= b.Instructions.Count) break;

                var fallThrough = FindBlockJump(b.Offsets[i + count], b);
                fallThrough.SourceIndex = i;
                points.Add(fallThrough);

                b.Sequences.Add(new Sequence(points, i, i + (count - 1)));
                i += count - 1;
            }
        }
    }

    private void FindBasicJumps()
    {
        for (var i = 0; i < Blocks.Count; i++)
        {
            var b = Blocks[i];
            var next = i + 1 < Blocks.Count ? Blocks[i + 1] : null;

            switch (b.Type)
            {
                case BlockType.JumpIfNot when b.Instructions.LastOrDefault() is EX_JumpIfNot jumpIfNot && next is not null:
                    b.Jumps.Add(new BlockJump(b, next));
                    b.Jumps.Add(FindBlockJump(jumpIfNot.CodeOffset, b));
                    break;

                case BlockType.BranchEndIfNot when next is not null:
                    b.Jumps.Add(new BlockJump(b, next));
                    break;

                case BlockType.Jump when b.Instructions.LastOrDefault() is EX_Jump jump:
                    b.Jumps.Add(FindBlockJump(jump.CodeOffset, b));
                    break;
            }
        }
    }

    private void ResolveEntryPointJumps()
    {
        if (Blocks.Count == 0) return;

        foreach (var point in EntryPoints)
            _entryPointJumps[point] = FindBlockJump(point, Blocks[0]);
    }

    public BlockJump GetEntryPointJump(uint point) =>
        _entryPointJumps.TryGetValue(point, out var jump)
            ? jump
            : throw new InvalidOperationException($"入口点 {point} 没有对应的块");

    private BlockJump FindBlockJump(uint offset, InstrBlock source)
    {
        var destination = Blocks.Find(b => b.IsInside(offset))
                          ?? throw new InvalidOperationException($"偏移 {offset} 不落在任何基本块里");

        var index = destination.GetIndex(offset);
        if (index < 0)
            throw new InvalidOperationException($"偏移 {offset} 落在块内但不在指令边界上");

        return new BlockJump(source, destination, offset);
    }
}
