using UAssetAPI.Kismet.Bytecode;
using SolsticeWinter.Decompiler.Model;

namespace SolsticeWinter.Decompiler.Nodes;

/// <summary>
/// Branch（`EX_JumpIfNot`）。移植自参考项目 <c>K2Node_IfThenElse</c>（MIT）。
/// 与我们的旧投影不同：它是**真正的语句节点**，两个 exec 出脚分别接 then/else 分支。
/// </summary>
public sealed class K2Node_IfThenElse : BPNode
{
    public K2Node_IfThenElse(GraphPin conditionPin, KismetExpression? instruction) : base("Branch", instruction)
    {
        conditionPin.SetName("Condition");
        ConditionPin = conditionPin;
        MakePins();
    }

    public GraphPin ConditionPin { get; }
    public GraphPin ExecElsePin { get; private set; } = null!;

    private void MakePins()
    {
        ExecPin = new GraphPin("execute", PinDirection.Input, GraphPinType.Exec) { IsNameHidden = true };
        ExecOutPin = new GraphPin("then", "True", PinDirection.Output, GraphPinType.Exec);
        ExecElsePin = new GraphPin("else", "False", PinDirection.Output, GraphPinType.Exec);

        AddInputPin(ExecPin);
        AddInputPin(ConditionPin);
        AddOutputPin(ExecOutPin);
        AddOutputPin(ExecElsePin);
    }
}

/// <summary>
/// 函数入口（`K2Node_FunctionEntry`）。UEBGV 的普通函数图都有这个节点，
/// 标题是函数名，参数以输出脚形式挂在右边。
/// </summary>
public sealed class K2Node_FunctionEntry : BPNode
{
    public K2Node_FunctionEntry(string functionName, IEnumerable<GraphPin> parameters, KismetExpression? instruction)
        : base(functionName, instruction)
    {
        ExecOutPin = new GraphPin("then", PinDirection.Output, GraphPinType.Exec) { IsNameHidden = true };
        AddOutputPin(ExecOutPin);
        foreach (var pin in parameters) AddOutputPin(pin);
    }
}

/// <summary>Sequence（多个 exec 出脚按顺序执行）。移植自 <c>K2Node_ExecutionSequence</c>。</summary>
public sealed class K2Node_ExecutionSequence : BPNode
{
    public K2Node_ExecutionSequence(int pinsCount, KismetExpression? instruction) : base("Sequence", instruction)
    {
        ExecPin = new GraphPin("execute", PinDirection.Input, GraphPinType.Exec) { IsNameHidden = true };
        AddInputPin(ExecPin);

        for (var i = 0; i < pinsCount; i++)
            AddOutputPin(new GraphPin($"Then {i}", PinDirection.Output, GraphPinType.Exec));
    }
}

/// <summary>
/// 循环（While / For 的字节码形态：条件 + JumpIfNot 退出 + 回边）。
///
/// <para>
/// UEBGV 把循环还原成**宏实例**（For Each Loop 等，靠 18 个宏 JSON 做模式匹配）；
/// 这里先做「字节码形态」的还原：一个 Loop 节点 + <c>LoopBody</c> / <c>Completed</c> 两个出脚。
/// 视觉上与 UE 的 While 节点一致，只是标题不带 ForEach。
/// </para>
/// </summary>
public sealed class K2Node_Loop : BPNode
{
    public K2Node_Loop(string title, KismetExpression? instruction) : base(title, instruction)
    {
        ExecPin = new GraphPin("execute", PinDirection.Input, GraphPinType.Exec) { IsNameHidden = true };
        BodyPin = new GraphPin("LoopBody", PinDirection.Output, GraphPinType.Exec);
        DonePin = new GraphPin("Completed", PinDirection.Output, GraphPinType.Exec);

        AddInputPin(ExecPin);
        AddOutputPin(BodyPin);
        AddOutputPin(DonePin);
    }

    public GraphPin BodyPin { get; }
    public GraphPin DonePin { get; }
}

/// <summary>函数返回（带返回值引脚）。移植自 <c>K2Node_FunctionResult</c>。</summary>
public sealed class K2Node_FunctionResult : BPNode
{
    public K2Node_FunctionResult(IEnumerable<GraphPin> outParams, KismetExpression? instruction)
        : base("Return Node", instruction)
    {
        MakePins(true, false, outParams);
    }
}

/// <summary>
/// 动态类型转换（`Cast To X`）。移植自 <c>K2Node_DynamicCast</c>：
/// exec 入 + then / Cast Failed 两个 exec 出 + 一个 `As X` 数据出脚。
/// </summary>
public sealed class K2Node_DynamicCast : BPNode
{
    public K2Node_DynamicCast(GraphPin objectPin, GraphPin asObjectPin, string className,
        KismetExpression? instruction, bool isInterface = false)
        : base($"Cast To {className}", instruction)
    {
        ClassName = className;
        ObjectPin = objectPin;
        AsObjectPin = asObjectPin;
        ObjectPin.SetName("Object");
        AsObjectPin.SetName($"As {className}");

        ExecPin = new GraphPin("execute", PinDirection.Input, GraphPinType.Exec) { IsNameHidden = true };
        ExecOutPin = new GraphPin("then", PinDirection.Output, GraphPinType.Exec) { IsNameHidden = true };
        ExecFailedPin = new GraphPin("CastFailed", "Cast Failed", PinDirection.Output, GraphPinType.Exec);

        AddInputPin(ExecPin);
        AddInputPin(ObjectPin);
        AddOutputPin(ExecOutPin);
        AddOutputPin(ExecFailedPin);
        AddOutputPin(AsObjectPin);
    }

    public string ClassName { get; }
    public GraphPin ObjectPin { get; }
    public GraphPin AsObjectPin { get; }
    public GraphPin ExecFailedPin { get; private set; }

    /// <summary>没有失败分支时退化成纯节点（与参考实现一致）。</summary>
    public void MakePure()
    {
        Pure = true;
        Output.Remove(ExecFailedPin);
        Output.Remove(ExecOutPin!);
        Input.Remove(ExecPin!);
        ExecFailedPin = null!;
        ExecOutPin = null;
        ExecPin = null;
    }
}

/// <summary>
/// 构造数组（`Make Array`）。移植自 <c>K2Node_MakeArray</c>：每个元素一个输入脚，一个数组输出脚。
/// </summary>
public sealed class K2Node_MakeArray : BPNode
{
    public K2Node_MakeArray(GraphPinType elementType, IEnumerable<GraphPin> elements, KismetExpression? instruction)
        : base("Make Array", instruction)
    {
        // Make Array 是纯节点：输出没人消费时应该被清理掉，否则会以孤立节点留在画布上
        // （实测 `GetNewLocationNumbers` 一下多出 28 个）。
        Pure = true;

        OutPin = new GraphPin("Array", PinDirection.Output,
            new GraphPinType(elementType.Category, Container: PinContainerType.Array, SubCategoryObject: elementType.SubCategoryObject));

        foreach (var element in elements) AddInputPin(element);
        AddOutputPin(OutPin);
    }

    public GraphPin OutPin { get; }
}

/// <summary>
/// 可折叠运算符（`+` / `==` / `AND` …）。移植自 <c>K2Node_PromotableOperator</c>：
/// 没有标题栏，符号直接当标题；参数脚不显示名字（UE 里就是这样）。
/// </summary>
public sealed class K2Node_PromotableOperator : BPNode
{
    public K2Node_PromotableOperator(string symbol, string functionName, IReadOnlyList<GraphPin> parameters,
        KismetExpression? instruction, bool isPure, GraphPin? returnPin = null)
        : base(symbol, instruction)
    {
        Symbol = symbol;
        FunctionName = functionName;

        // UE 的运算符节点输入脚叫 A / B（不是空名）。之前照 UEBGV 全部清空，
        // 画布上就是三个无名引脚，读不出哪个是左操作数。
        for (var i = 0; i < parameters.Count; i++)
            parameters[i].SetName(((char)('A' + i)).ToString());

        MakePins(!isPure, !isPure, parameters);

        if (returnPin is not null)
        {
            returnPin.IsNameHidden = true;   // 输出脚不显示名字（UE 同款），类型照常显示
            AddOutputPin(returnPin);
        }

        Pure = isPure;
    }

    public string Symbol { get; }
    public string FunctionName { get; }
}

/// <summary>
/// 构造结构体（`Make CampaignCardStats`）。移植自 <c>K2Node_MakeStruct</c>：
/// 每个字段一个输入脚，一个结构体类型的输出脚。
/// <summary>
/// <summary>
/// <summary>
/// 宏实例（`For Each Loop` 等）。移植自 <c>K2Node_MacroInstance</c>：
/// UE 编辑器里这些宏是**一个**节点，但字节码里是内联展开的一串指令。
/// </summary>
public sealed class K2Node_MacroInstance : BPNode
{
    public K2Node_MacroInstance(string macroName, GraphPin? arrayPin, GraphPin? indexPin,
        GraphPin? elementPin, GraphPin? bodyOutPin, GraphPin? conditionPin = null)
        : base(macroName, null)
    {
        ExecPin = new GraphPin("execute", PinDirection.Input, GraphPinType.Exec) { IsNameHidden = true };
        ThenPin = new GraphPin("LoopBody", PinDirection.Output, GraphPinType.Exec);
        CompletedPin = new GraphPin("Completed", PinDirection.Output, GraphPinType.Exec);

        AddInputPin(ExecPin);

        if (arrayPin is not null)
        {
            ArrayPin = new GraphPin("Array", PinDirection.Input, arrayPin.Type);
            ElementPin = new GraphPin("Array Element", PinDirection.Output, elementPin?.Type ?? arrayPin.Type);
            IndexPin = new GraphPin("Array Index", PinDirection.Output,
                indexPin?.Type ?? new GraphPinType(PinType.Int));

            AddInputPin(ArrayPin);
            GraphPin.Connect(arrayPin, ArrayPin);
        }

        if (conditionPin is not null)
        {
            ConditionPin = new GraphPin("Condition", PinDirection.Input, conditionPin.Type);
            AddInputPin(ConditionPin);
            GraphPin.Connect(conditionPin, ConditionPin);
        }

        AddOutputPin(ThenPin);
        if (ElementPin is not null) AddOutputPin(ElementPin);
        if (IndexPin is not null) AddOutputPin(IndexPin);
        AddOutputPin(CompletedPin);

        if (bodyOutPin is not null)
        {
            // 必须走 `GraphPin.Connect`：只往 `ThenPin.LinkedTo` 里塞，消费者的
            // `Source`（派生自它自己的 `LinkedTo`）就还是 null，连线在渲染时是断的。
            foreach (var consumer in bodyOutPin.LinkedTo.Where(p => p.IsInput).ToList())
            {
                GraphPin.Connect(ThenPin, consumer);
            }
        }
    }

    public GraphPin? ArrayPin { get; }
    public GraphPin? ConditionPin { get; }
    public GraphPin ThenPin { get; }
    public GraphPin CompletedPin { get; }
    public GraphPin? ElementPin { get; }
    public GraphPin? IndexPin { get; }
}

/// 局部变量声明（`Local Int (Loop Counter)`）。移植自 <c>K2Node_TemporaryVariable</c>：
/// UE 为编译器临时变量（`Temp_` 前缀）单独发一个声明节点。
/// </summary>
public sealed class K2Node_TemporaryVariable : BPNode
{
    public K2Node_TemporaryVariable(string variableName, GraphPinType type, KismetExpression? instruction)
        // 编译器临时变量（`CallFunc_*` / `K2Node_*`）的内部名不该露出给用户看：
        // 之前标题是 `Local Double (CallFunc_BreakVector2D_X)`，明显是编译器垃圾。
        // 显示成 `Local Double` 就够了，完整名字留在 `VariableName`/工具提示里。
        : base(IsCompilerGenerated(variableName)
                ? $"Local {type.Display()}"
                : $"Local {type.Display()} ({variableName})",
              instruction)
    {
        VariableName = variableName;
        HeaderHidden = true;
        Pure = true;
        VarPin = new GraphPin(variableName, PinDirection.Output, type);
        AddOutputPin(VarPin);
    }

    private static bool IsCompilerGenerated(string name) =>
        name.StartsWith("CallFunc_", StringComparison.Ordinal)
        || name.StartsWith("K2Node_", StringComparison.Ordinal);

    /// <summary>类型改变后刷新标题（标题里嵌了类型）。编译器生成的内部名不显示。</summary>
    public void RefreshTitle() =>
        Name = IsCompilerGenerated(VariableName)
            ? $"Local {VarPin.Type.Display()}"
            : $"Local {VarPin.Type.Display()} ({VariableName})";

    public string VariableName { get; }
    public GraphPin VarPin { get; }
}

/// 赋值语句（`Assign`）。移植自 <c>K2Node_AssignmentStatement</c>：
/// UE 对编译器临时变量（`Temp_` 前缀）的赋值会发这个节点，而不是 VariableSet。
/// </summary>
public sealed class K2Node_AssignmentStatement : BPNode
{
    public K2Node_AssignmentStatement(GraphPin variablePin, GraphPin valuePin, KismetExpression? instruction)
        : base(variablePin.Name.Length > 0 ? $"Set {variablePin.Name}" : "Assign", instruction)
    {
        VariablePin = variablePin;
        ValuePin = valuePin;

        ExecPin = new GraphPin("execute", PinDirection.Input, GraphPinType.Exec) { IsNameHidden = true };
        ExecOutPin = new GraphPin("then", PinDirection.Output, GraphPinType.Exec) { IsNameHidden = true };
        AddInputPin(ExecPin);
        AddInputPin(variablePin);
        AddInputPin(valuePin);
        AddOutputPin(ExecOutPin);
    }

    public GraphPin VariablePin { get; }
    public GraphPin ValuePin { get; }
}

/// </summary>
public sealed class K2Node_MakeStruct : BPNode
{
    public K2Node_MakeStruct(string structName, IEnumerable<GraphPin> fields, KismetExpression? instruction)
        : base($"Make {structName}", instruction)
    {
        StructName = structName;
        OutPin = new GraphPin(structName, PinDirection.Output,
            new GraphPinType(PinType.Struct, SubCategoryObject: structName));

        foreach (var field in fields) AddInputPin(field);
        AddOutputPin(OutPin);
    }

    public string StructName { get; }
    public GraphPin OutPin { get; }
}
/// <summary>
/// 读变量。移植自 <c>K2Node_VariableGet</c>。
///
/// <para>
/// UE 编辑器里 Get 节点有**标题栏，写变量名**，输出脚不再重复名字。
/// 之前照 UEBGV 做成「无标题 + 名字在脚上」，画布上就是一排空白小方块，比编辑器难读得多。
/// </para>
/// </summary>
public sealed class K2Node_VariableGet : BPNode
{
    public K2Node_VariableGet(PropertyRef property, GraphPin? targetPin, KismetExpression? instruction)
        : base(property.Name, instruction)
    {
        Property = property;
        Pure = true;
        VarPin = new GraphPin(property.Name, PinDirection.Output, property.PinType) { IsNameHidden = true };
        AddOutputPin(VarPin);
        if (targetPin is not null) AddInputPin(targetPin);
    }

    public PropertyRef Property { get; }
    public GraphPin VarPin { get; }
}

/// <summary>
/// 拆分结构体（`Break CampaignCardStats`）。移植自 <c>K2Node_BreakStruct</c>：
/// UE 里读结构体字段是**一个**节点，每个字段一个输出脚；字节码里却是每个字段一条读指令。
/// </summary>
public sealed class K2Node_BreakStruct : BPNode
{
    public K2Node_BreakStruct(string structName, GraphPin sourcePin, IEnumerable<string> fieldNames,
        KismetExpression? instruction)
        : base($"Break {structName}", instruction)
    {
        StructName = structName;
        Pure = true;

        InputPin = new GraphPin("Struct", PinDirection.Input, sourcePin.Type);
        AddInputPin(InputPin);
        GraphPin.Connect(sourcePin, InputPin);

        foreach (var fieldName in fieldNames)
        {
            var pin = new GraphPin(fieldName, PinDirection.Output, new GraphPinType(PinType.Unknown));
            AddOutputPin(pin);
        }
    }

    public string StructName { get; }
    public GraphPin InputPin { get; }

    /// <summary>按字段名取输出脚。</summary>
    public GraphPin? OutputFor(string fieldName) =>
        Output.FirstOrDefault(p => string.Equals(p.Name, fieldName, StringComparison.Ordinal));
}

/// <summary>写变量（有 exec 入/出）。移植自 <c>K2Node_VariableSet</c>。</summary>
public sealed class K2Node_VariableSet : BPNode
{
    public K2Node_VariableSet(PropertyRef property, GraphPin valuePin, GraphPin? contextPin, KismetExpression? instruction)
        : base($"Set {property.Name}", instruction)
    {
        Property = property;
        CompactTitle = true;
        ValuePin = valuePin;
        ContextPin = contextPin;
        MakePins(true, true);
        // 变量名已经在标题里了，值脚不再重复（UE 同款）
        ValuePin.SetName(string.Empty);
        ValuePin.IsNameHidden = true;
        AddInputPin(ValuePin);
    }

    public PropertyRef Property { get; }
    public GraphPin ValuePin { get; }
    public GraphPin? ContextPin { get; }
}

/// <summary>
/// 函数调用。移植自 <c>K2Node_CallFunction</c>：纯函数（无 exec）与 impure（有 exec）由签名决定。
/// 参考项目里那些「数学/字符串库特殊外观」的映射表（<c>PromotableOperators</c> 等）**尚未移植**，
/// 所以这里显示的是函数名，不是运算符。
/// </summary>
public sealed class K2Node_CallFunction : BPNode
{
    public K2Node_CallFunction(string functionName, string ownerPath, IReadOnlyList<GraphPin> parameters,
        KismetExpression? instruction, bool isPure, GraphPin? returnPin = null, string? displayName = null)
        // 标题只放函数名。UE 里节点标题就是 `GetCardFromID`，归属类靠**标题栏颜色**区分；
        // 之前写成 `GetCardFromID (BP_CardFunctions_C)`，又长又重复——归属类已经在副标题里了。
        : base(displayName is { Length: > 0 } ? displayName : functionName, instruction)
    {
        FunctionName = functionName;
        OwnerPath = ownerPath;
        MakePins(!isPure, !isPure, parameters);

        // 纯调用（作为值被引用）必须有输出脚，否则整节点无处可连、成为孤立节点。
        // 签名未知时拿不到返回脚，就补一个占位 ReturnValue（实测 GetViewportSize 这种多 out 参数的函数）。
        if (isPure && returnPin is null && parameters.All(p => p.Direction != PinDirection.Output))
            returnPin = new GraphPin("ReturnValue", PinDirection.Output, new GraphPinType(PinType.Unknown));
        if (returnPin is not null) AddOutputPin(returnPin);
        Pure = isPure;
    }

    public string FunctionName { get; }
    public string OwnerPath { get; }

    private static string ShortOwner(string ownerPath)
    {
        var dot = ownerPath.LastIndexOf('.');
        return dot >= 0 ? ownerPath[(dot + 1)..] : ownerPath;
    }
}


/// <summary>
/// 认不出的表达式：仍然占一个节点，标成不透明，方便定位「哪里没反编译出来」。
/// （原本定义在旧 FunctionWalker.cs 里，重写时搬到这里。）
/// </summary>
public sealed class UnknownNode : BPNode
{
    public UnknownNode(string name, KismetExpression? instruction) : base(name, instruction)
    {
        MakePins(true, true);
    }
}


/// <summary>
/// 委托操作（`Bind Event to X` / `Add X` / `Remove X` / `Clear X`）。
///
/// <para>
/// 字节码里是 `EX_BindDelegate` / `EX_AddMulticastDelegate` / `EX_RemoveMulticastDelegate` /
/// `EX_ClearMulticastDelegate` 这些独立指令。之前完全没处理，导致 12 条语句被丢掉
/// （实测 BP_Logic 有 7 + 3 + 1 + 1 条）。
/// </para>
/// </summary>
public sealed class K2Node_DelegateOp : BPNode
{
    public K2Node_DelegateOp(string title, GraphPin? delegatePin, GraphPin? handlerPin,
        KismetExpression? instruction, bool multicast)
        : base(title, instruction)
    {
        if (multicast)
        {
            MakePins(needExec: true, needThen: true);
            if (delegatePin is not null) AddInputPin(delegatePin);
            if (handlerPin is not null) AddInputPin(handlerPin);
        }
        else
        {
            MakePins(needExec: true, needThen: true);
            if (delegatePin is not null) AddInputPin(delegatePin);
            if (handlerPin is not null) AddInputPin(handlerPin);
        }
    }
}

