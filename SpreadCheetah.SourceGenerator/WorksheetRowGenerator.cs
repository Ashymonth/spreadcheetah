using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SpreadCheetah.SourceGenerator;
using SpreadCheetah.SourceGenerator.Helpers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SpreadCheetah.SourceGenerators;

[Generator]
public class WorksheetRowGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var filtered = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (s, _) => IsSyntaxTargetForGeneration(s),
                static (ctx, token) => GetSemanticTargetForGeneration(ctx, token))
            .Where(static x => x is not null)
            .Collect();

        var source = context.CompilationProvider.Combine(filtered);

        context.RegisterSourceOutput(source, static (spc, source) => Execute(source.Left, source.Right, spc));
    }

    private static bool IsSyntaxTargetForGeneration(SyntaxNode syntaxNode) => syntaxNode is ClassDeclarationSyntax
    {
        AttributeLists.Count: > 0,
        BaseList.Types.Count: > 0
    };

    private static INamedTypeSymbol? GetWorksheetRowAttributeType(Compilation compilation)
    {
        return compilation.GetTypeByMetadataName("SpreadCheetah.SourceGeneration.WorksheetRowAttribute");
    }

    private static INamedTypeSymbol? GetGenerationOptionsAttributeType(Compilation compilation)
    {
        return compilation.GetTypeByMetadataName("SpreadCheetah.SourceGeneration.WorksheetRowGenerationOptionsAttribute");
    }

    private static INamedTypeSymbol? GetContextBaseType(Compilation compilation)
    {
        return compilation.GetTypeByMetadataName("SpreadCheetah.SourceGeneration.WorksheetRowContext");
    }

    private static ContextClass? GetSemanticTargetForGeneration(GeneratorSyntaxContext context, CancellationToken token)
    {
        if (context.Node is not ClassDeclarationSyntax classDeclaration)
            return null;

        if (!classDeclaration.Modifiers.Any(static x => x.IsKind(SyntaxKind.PartialKeyword)))
            return null;

        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration, token);
        if (classSymbol is null)
            return null;

        if (classSymbol.IsStatic)
            return null;

        var baseType = classSymbol.BaseType;
        if (baseType is null)
            return null;

        var baseContext = GetContextBaseType(context.SemanticModel.Compilation);
        if (baseContext is null)
            return null;

        if (!SymbolEqualityComparer.Default.Equals(baseContext, baseType))
            return null;

        var worksheetRowAttribute = GetWorksheetRowAttributeType(context.SemanticModel.Compilation);
        if (worksheetRowAttribute is null)
            return null;

        var optionsAttribute = GetGenerationOptionsAttributeType(context.SemanticModel.Compilation);
        if (optionsAttribute is null)
            return null;

        var rowTypes = new Dictionary<INamedTypeSymbol, Location>(SymbolEqualityComparer.Default);
        GeneratorOptions? generatorOptions = null;

        foreach (var attribute in classSymbol.GetAttributes())
        {
            if (TryParseWorksheetRowAttribute(attribute, worksheetRowAttribute, token, out var typeSymbol, out var location)
                && !rowTypes.ContainsKey(typeSymbol))
            {
                rowTypes[typeSymbol] = location;
                continue;
            }

            if (TryParseOptionsAttribute(attribute, optionsAttribute, out var options))
                generatorOptions = options;
        }

        return rowTypes.Count > 0
            ? new ContextClass(classSymbol, rowTypes, generatorOptions)
            : null;
    }

    private static bool TryParseWorksheetRowAttribute(
        AttributeData attribute,
        INamedTypeSymbol expectedAttribute,
        CancellationToken token,
        [NotNullWhen(true)] out INamedTypeSymbol? typeSymbol,
        [NotNullWhen(true)] out Location? location)
    {
        typeSymbol = null;
        location = null;

        if (!SymbolEqualityComparer.Default.Equals(expectedAttribute, attribute.AttributeClass))
            return false;

        var args = attribute.ConstructorArguments;
        if (args.Length != 1)
            return false;

        if (args[0].Value is not INamedTypeSymbol symbol)
            return false;

        if (symbol.Kind == SymbolKind.ErrorType)
            return false;

        var syntaxReference = attribute.ApplicationSyntaxReference;
        if (syntaxReference is null)
            return false;

        location = syntaxReference.GetSyntax(token).GetLocation();
        typeSymbol = symbol;
        return true;
    }

    private static bool TryParseOptionsAttribute(
        AttributeData attribute,
        INamedTypeSymbol expectedAttribute,
        [NotNullWhen(true)] out GeneratorOptions? options)
    {
        options = null;

        if (!SymbolEqualityComparer.Default.Equals(expectedAttribute, attribute.AttributeClass))
            return false;

        if (attribute.NamedArguments.IsDefaultOrEmpty)
            return false;

        foreach (var arg in attribute.NamedArguments)
        {
            if (!string.Equals(arg.Key, "SuppressWarnings", StringComparison.Ordinal))
                continue;

            if (arg.Value.Value is bool suppressWarnings)
            {
                options = new GeneratorOptions(suppressWarnings);
                return true;
            }
        }

        return false;
    }

    private static TypePropertiesInfo AnalyzeTypeProperties(Compilation compilation, ITypeSymbol classType)
    {
        var propertyNames = new List<string>();
        var unsupportedPropertyNames = new List<IPropertySymbol>();

        foreach (var member in classType.GetMembers())
        {
            if (member is not IPropertySymbol
                {
                    DeclaredAccessibility: Accessibility.Public,
                    IsStatic: false,
                    IsWriteOnly: false
                } p)
            {
                continue;
            }

            if (p.Type.SpecialType == SpecialType.System_String
                || SupportedPrimitiveTypes.Contains(p.Type.SpecialType)
                || IsSupportedNullableType(compilation, p.Type))
            {
                propertyNames.Add(p.Name);
            }
            else
            {
                unsupportedPropertyNames.Add(p);
            }
        }

        return new TypePropertiesInfo(propertyNames, unsupportedPropertyNames);
    }

    private static bool IsSupportedNullableType(Compilation compilation, ITypeSymbol type)
    {
        //check if it's a type with nullable annotation
        if (type.NullableAnnotation == NullableAnnotation.Annotated)
            return true;

        if (type.SpecialType != SpecialType.System_Nullable_T)
            return false;

        var nullableT = compilation.GetTypeByMetadataName("System.Nullable`1");

        foreach (var primitiveType in SupportedPrimitiveTypes)
        {
            var nullableType = nullableT?.Construct(compilation.GetSpecialType(primitiveType));
            if (nullableType is null)
                continue;

            if (nullableType.Equals(type, SymbolEqualityComparer.Default))
                return true;
        }

        return false;
    }

    private static readonly SpecialType[] SupportedPrimitiveTypes =
    {
        SpecialType.System_Boolean,
        SpecialType.System_DateTime,
        SpecialType.System_Decimal,
        SpecialType.System_Double,
        SpecialType.System_Int32,
        SpecialType.System_Int64,
        SpecialType.System_Single
    };

    private static void Execute(Compilation compilation, ImmutableArray<ContextClass?> classes, SourceProductionContext context)
    {
        if (classes.IsDefaultOrEmpty)
            return;

        var sb = new StringBuilder();

        foreach (var item in classes)
        {
            if (item is null) continue;

            context.CancellationToken.ThrowIfCancellationRequested();

            sb.Clear();
            GenerateCode(sb, item, compilation, context);
            context.AddSource($"{item.ContextClassType}.g.cs", sb.ToString());
        }
    }

    private static void GenerateHeader(StringBuilder sb)
    {
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using SpreadCheetah;");
        sb.AppendLine("using SpreadCheetah.SourceGeneration;");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
    }

    private static void GenerateCode(StringBuilder sb, ContextClass contextClass, Compilation compilation, SourceProductionContext context)
    {
        GenerateHeader(sb);

        var contextType = contextClass.ContextClassType;
        var contextTypeNamespace = contextType.ContainingNamespace;
        if (contextTypeNamespace is { IsGlobalNamespace: false })
            sb.AppendLine($"namespace {contextTypeNamespace}");

        var accessibility = SyntaxFacts.GetText(contextType.DeclaredAccessibility);

        sb.AppendLine("{");
        sb.AppendLine($"    {accessibility} partial class {contextType.Name}");
        sb.AppendLine("    {");
        sb.AppendLine($"        private static {contextType.Name}? _default;");
        sb.AppendLine($"        public static {contextType.Name} Default => _default ??= new {contextType.Name}();");
        sb.AppendLine();
        sb.AppendLine($"        public {contextType.Name}()");
        sb.AppendLine("        {");
        sb.AppendLine("        }");

        var rowTypeNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var keyValue in contextClass.RowTypes)
        {
            var rowType = keyValue.Key;
            var rowTypeName = rowType.Name;
            if (!rowTypeNames.Add(rowTypeName))
                continue;

            var rowTypeFullName = rowType.ToString();
            var location = keyValue.Value;

            sb.AppendLine();
            sb.AppendLine(2, $"private WorksheetRowTypeInfo<{rowTypeFullName}>? _{rowTypeName};");
            sb.AppendLine(2, $"public WorksheetRowTypeInfo<{rowTypeFullName}> {rowTypeName} => _{rowTypeName} ??= WorksheetRowMetadataServices.CreateObjectInfo<{rowTypeFullName}>(AddAsRowAsync, AddRangeAsRowsAsync);");

            var info = AnalyzeTypeProperties(compilation, rowType);
            ReportDiagnostics(info, rowType, location, contextClass.Options, context);

            GenerateAddAsRow(sb, 2, rowType, info.PropertyNames);
            GenerateAddRangeAsRows(sb, 2, rowType, info.PropertyNames);

            if (info.PropertyNames.Count == 0)
            {
                GenerateAddRangeAsEmptyRows(sb, 2, rowType);
                continue;
            }

            GenerateAddAsRowInternal(sb, 2, rowTypeFullName, info.PropertyNames);
            GenerateAddRangeAsRowsInternal(sb, rowType, info.PropertyNames);
            GenerateAddEnumerableAsRows(sb, 2, rowType);
            GenerateAddCellsAsRow(sb, 2, rowType, info.PropertyNames);
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static void ReportDiagnostics(TypePropertiesInfo info, INamedTypeSymbol rowType, Location location, GeneratorOptions? options, SourceProductionContext context)
    {
        if (options?.SuppressWarnings ?? false) return;

        if (info.PropertyNames.Count == 0)
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoPropertiesFound, location, rowType.Name));

        if (info.UnsupportedProperties.FirstOrDefault() is { } unsupportedProperty)
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedTypeForCellValue, location, rowType.Name, unsupportedProperty.Type.Name));
    }

    private static void GenerateAddAsRow(StringBuilder sb, int indent, INamedTypeSymbol rowType, List<string> propertyNames)
    {
        sb.AppendLine()
            .AppendIndentation(indent)
            .Append("private static ValueTask AddAsRowAsync(SpreadCheetah.Spreadsheet spreadsheet, ")
            .AppendType(rowType)
            .AppendLine(" obj, CancellationToken token)");

        sb.AppendLine(indent, "{");
        sb.AppendLine(indent, "    if (spreadsheet is null)");
        sb.AppendLine(indent, "        throw new ArgumentNullException(nameof(spreadsheet));");

        if (propertyNames.Count == 0)
        {
            sb.AppendLine(indent, "    return spreadsheet.AddRowAsync(ReadOnlyMemory<DataCell>.Empty, token);");
            sb.AppendLine(indent, "}");
            return;
        }

        if (rowType.IsReferenceType)
        {
            sb.AppendLine(indent + 1, "if (obj is null)");
            sb.AppendLine(indent + 1, "    return spreadsheet.AddRowAsync(ReadOnlyMemory<DataCell>.Empty, token);");
        }

        sb.AppendLine(indent, "    return AddAsRowInternalAsync(spreadsheet, obj, token);");
        sb.AppendLine(indent, "}");
    }

    private static void GenerateAddAsRowInternal(StringBuilder sb, int indent, string rowTypeFullname, List<string> propertyNames)
    {
        sb.AppendLine();
        sb.AppendLine(indent, $"private static async ValueTask AddAsRowInternalAsync(SpreadCheetah.Spreadsheet spreadsheet, {rowTypeFullname} obj, CancellationToken token)");
        sb.AppendLine(indent, "{");
        sb.AppendLine(indent, $"    var cells = ArrayPool<DataCell>.Shared.Rent({propertyNames.Count});");
        sb.AppendLine(indent, "    try");
        sb.AppendLine(indent, "    {");
        sb.AppendLine(indent, "        await AddCellsAsRowAsync(spreadsheet, obj, cells, token).ConfigureAwait(false);");
        sb.AppendLine(indent, "    }");
        sb.AppendLine(indent, "    finally");
        sb.AppendLine(indent, "    {");
        sb.AppendLine(indent, "        ArrayPool<DataCell>.Shared.Return(cells, true);");
        sb.AppendLine(indent, "    }");
        sb.AppendLine(indent, "}");
    }

    private static void GenerateAddRangeAsRows(StringBuilder sb, int indent, INamedTypeSymbol rowType, List<string> propertyNames)
    {
        sb.AppendLine()
            .AppendIndentation(indent)
            .Append("private static ValueTask AddRangeAsRowsAsync(SpreadCheetah.Spreadsheet spreadsheet, IEnumerable<")
            .AppendType(rowType)
            .AppendLine("> objs, CancellationToken token)");

        sb.AppendLine(indent, "{");
        sb.AppendLine(indent, "    if (spreadsheet is null)");
        sb.AppendLine(indent, "        throw new ArgumentNullException(nameof(spreadsheet));");
        sb.AppendLine(indent, "    if (objs is null)");
        sb.AppendLine(indent, "        throw new ArgumentNullException(nameof(objs));");

        if (propertyNames.Count == 0)
            sb.AppendLine(indent, "    return AddRangeAsEmptyRowsAsync(spreadsheet, objs, token);");
        else
            sb.AppendLine(indent, "    return AddRangeAsRowsInternalAsync(spreadsheet, objs, token);");

        sb.AppendLine(indent, "}");
    }

    private static void GenerateAddRangeAsEmptyRows(StringBuilder sb, int indent, INamedTypeSymbol rowType)
    {
        sb.AppendLine()
            .AppendIndentation(indent)
            .Append("private static async ValueTask AddRangeAsEmptyRowsAsync(SpreadCheetah.Spreadsheet spreadsheet, IEnumerable<")
            .AppendType(rowType)
            .AppendLine("> objs, CancellationToken token)");

        sb.AppendLine(indent, "{");
        sb.AppendLine(indent, "    foreach (var _ in objs)");
        sb.AppendLine(indent, "    {");
        sb.AppendLine(indent, "        await spreadsheet.AddRowAsync(ReadOnlyMemory<DataCell>.Empty, token);");
        sb.AppendLine(indent, "    }");
        sb.AppendLine(indent, "}");
    }

    private static void GenerateAddRangeAsRowsInternal(StringBuilder sb, INamedTypeSymbol rowType, List<string> propertyNames)
    {
        var typeString = rowType.ToTypeString();
        sb.Append($$"""

                private static async ValueTask AddRangeAsRowsInternalAsync(SpreadCheetah.Spreadsheet spreadsheet, IEnumerable<{{typeString}}> objs, CancellationToken token)
                {
                    var cells = ArrayPool<DataCell>.Shared.Rent({{propertyNames.Count}});
                    try
                    {
                        await AddEnumerableAsRowsAsync(spreadsheet, objs, cells, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<DataCell>.Shared.Return(cells, true);
                    }
                }

        """);
    }

    private static void GenerateAddEnumerableAsRows(StringBuilder sb, int indent, INamedTypeSymbol rowType)
    {
        sb.AppendLine()
            .AppendIndentation(indent)
            .Append("private static async ValueTask AddEnumerableAsRowsAsync(SpreadCheetah.Spreadsheet spreadsheet, IEnumerable<")
            .AppendType(rowType)
            .AppendLine("> objs, DataCell[] cells, CancellationToken token)");

        sb.AppendLine(indent, "{");
        sb.AppendLine(indent, "    foreach (var obj in objs)");
        sb.AppendLine(indent, "    {");
        sb.AppendLine(indent, "        await AddCellsAsRowAsync(spreadsheet, obj, cells, token).ConfigureAwait(false);");
        sb.AppendLine(indent, "    }");
        sb.AppendLine(indent, "}");
    }

    private static void GenerateAddCellsAsRow(StringBuilder sb, int indent, INamedTypeSymbol rowType, List<string> propertyNames)
    {
        sb.AppendLine()
            .AppendIndentation(indent)
            .Append("private static ValueTask AddCellsAsRowAsync(SpreadCheetah.Spreadsheet spreadsheet, ")
            .AppendType(rowType)
            .AppendLine(" obj, DataCell[] cells, CancellationToken token)");

        sb.AppendLine(indent, "{");

        if (rowType.IsReferenceType)
        {
            sb.AppendLine(indent, "    if (obj is null)");
            sb.AppendLine(indent, "        return spreadsheet.AddRowAsync(ReadOnlyMemory<DataCell>.Empty, token);");
            sb.AppendLine();
        }

        for (var i = 0; i < propertyNames.Count; ++i)
        {
            sb.AppendIndentation(indent + 1)
                .Append("cells[")
                .Append(i)
                .Append("] = new DataCell(obj.")
                .Append(propertyNames[i])
                .AppendLine(");");
        }

        sb.AppendLine(indent, $"    return spreadsheet.AddRowAsync(cells.AsMemory(0, {propertyNames.Count}), token);");
        sb.AppendLine(indent, "}");
    }
}
