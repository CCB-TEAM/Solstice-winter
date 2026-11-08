using UAssetAPI.Kismet.Bytecode;

namespace SolsticeWinter.Decompiler.Model;

/// <summary>
/// 画布节点基类。移植自 <c>UEBlueprintGraphViewer.Nodes.BPNode</c>（MIT）。
///
/// <para>
/// 与参考实现一致：节点自己管引脚顺序与尺寸估算，锚点由引脚按序号算。
/// 差异：去掉了 diff/JSON 等编辑与比较期才用的成员；<see cref="Instruction"/> 用 UAssetAPI 的
/// <see cref="KismetExpression"/>（参考项目用 CUE4Parse 的同名类型）。
/// </para>
/// </summary>
public class BPNode
{
    public BPNode() { }

    public BPNode(string name, KismetExpression? instruction)
    {
        Name = name;
        Instruction = instruction;
        NodeType = GetType().Name;
    }

    /// <summary>产生这个节点的字节码指令（调试与错误定位用）。</summary>
    public KismetExpression? Instruction { get; init; }

    public string NodeType { get; init; } = nameof(BPNode);

    public string Name { get; set; } = string.Empty;

    public float X { get; set; }
    public float Y { get; set; }
    public int NodeWidth { get; set; }
    public int NodeHeight { get; set; }

    public float Right => X + NodeWidth;
    public float Bottom => Y + NodeHeight;

    public GraphPin? ExecPin { get; protected set; }
    public GraphPin? ExecOutPin { get; protected set; }

    public bool HeaderHidden { get; set; }
    public bool ShowNameAsBody { get; set; }
    public bool CompactTitle { get; set; }
    public bool Pure { get; set; }

    public List<GraphPin> Input { get; } = [];
    public List<GraphPin> Output { get; } = [];

    public int InputParamsCount => Input.Count - (ExecPin is null ? 0 : 1);
    public int OutputParamsCount => Output.Count - (ExecOutPin is null ? 0 : 1);

    public void AddInputPin(GraphPin pin)
    {
        pin.BindToNode(this);
        Input.Add(pin);
    }

    public void AddOutputPin(GraphPin pin)
    {
        pin.BindToNode(this);
        Output.Add(pin);
    }

    public GraphPin? GetFirstInputParam() => Input.Find(p => !ReferenceEquals(p, ExecPin));
    public GraphPin? GetFirstOutputParam() => Output.Find(p => !ReferenceEquals(p, ExecOutPin));

    public void SetPosition(float x, float y)
    {
        X = x;
        Y = y;
        foreach (var pin in Input) pin.UpdateAnchor();
        foreach (var pin in Output) pin.UpdateAnchor();
    }

    /// <summary>
    /// 尺寸估算：宽度按「最长的输入名 + 值框 + 正文 + 最长的输出名」，
    /// 高度按「头 + 输入/输出引脚数的较大者」。公式与参考项目一致，方便对照。
    /// </summary>
    public void CalculateSize()
    {
        const int charSize = 7;
        const int charSizeBodyText = 15;
        const int padding = 32;
        const int valueBoxMinSize = 30;
        const int valueBoxMaxSize = 400;
        const int pinHeight = 32;

        var inputSize = 0;
        var outputSize = 0;
        var inputPinsHeight = 0;
        var outputPinsHeight = 0;

        NodeHeight = HeaderHidden ? 0 : 34;

        foreach (var pin in Input)
        {
            if (pin.IsHidden) continue;
            inputPinsHeight += pinHeight;

            var nameSize = pin.IsNameHidden ? 0 : pin.FriendlyName.Length * charSize;
            var valueSize = 0;
            if (pin.LinkedTo.Count == 0)
                valueSize = Math.Clamp(pin.Value.Length * charSize + 16, valueBoxMinSize, valueBoxMaxSize) + 4;

            inputSize = Math.Max(inputSize, nameSize + valueSize);
        }

        foreach (var pin in Output)
        {
            if (pin.IsHidden) continue;
            outputPinsHeight += pinHeight;
            outputSize = Math.Max(outputSize, pin.IsNameHidden ? 0 : pin.FriendlyName.Length * charSize);
        }

        var inputPadding = inputPinsHeight == 0 ? 0 : padding;
        var outputPadding = outputPinsHeight == 0 ? 0 : padding;

        var bodySize = 35;
        if (ShowNameAsBody) bodySize = 12 + Math.Max(23, Name.Length * charSizeBodyText);

        NodeWidth = inputPadding + inputSize + bodySize + outputSize + outputPadding;
        NodeWidth = Math.Max(NodeWidth, Name.Length * charSize + 20);
        if (CompactTitle) NodeWidth = Math.Max(NodeWidth, Name.Length * charSize + 80);

        // UE 编辑器里节点宽度是有上限的（约 300），长函数名靠截断 + tooltip，
        // 否则长名字会把节点撑到 500+，大图里互相压（实测 327 节点的图明显挤）
        NodeWidth = Math.Clamp(NodeWidth, 160, 320);
        NodeHeight += Math.Max(inputPinsHeight, outputPinsHeight);
    }

    /// <summary>按「是否需要 exec 入/出」补引脚，再把参数引脚按方向加进去。</summary>
    protected void MakePins(bool needExec, bool needThen, IEnumerable<GraphPin>? parameters = null, GraphPin? contextPin = null)
    {
        Pure = !needExec && !needThen;

        if (needExec)
        {
            ExecPin = new GraphPin("execute", PinDirection.Input, GraphPinType.Exec) { IsNameHidden = true };
            AddInputPin(ExecPin);
        }

        if (needThen)
        {
            ExecOutPin = new GraphPin("then", PinDirection.Output, GraphPinType.Exec) { IsNameHidden = true };
            AddOutputPin(ExecOutPin);
        }

        if (contextPin is not null) AddInputPin(contextPin);

        foreach (var pin in parameters ?? [])
        {
            if (pin.Direction == PinDirection.Input) AddInputPin(pin);
            else AddOutputPin(pin);
        }
    }
}
