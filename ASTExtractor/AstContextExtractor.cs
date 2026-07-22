using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TreeSitter;

namespace ASTExtractor
{
    // =========================================================================
    // JSON schema models — decorated for System.Text.Json
    // =========================================================================

    public class SourceRange
    {
        [JsonPropertyName("start_line")]
        public int StartLine { get; set; }

        [JsonPropertyName("end_line")]
        public int EndLine { get; set; }
    }

    public class TargetFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("signature")]
        public string Signature { get; set; }

        [JsonPropertyName("doc_comment")]
        public string DocComment { get; set; }

        [JsonPropertyName("source_range")]
        public SourceRange SourceRange { get; set; }

        [JsonPropertyName("body_code")]
        public string BodyCode { get; set; }
    }

    public class TypeDependency
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("definition")]
        public string Definition { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }
    }

    public class CalleeDependency
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("signature")]
        public string Signature { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }
    }

    public class ContextDependencies
    {
        [JsonPropertyName("types")]
        public List<TypeDependency> Types { get; set; }

        [JsonPropertyName("callees")]
        public List<CalleeDependency> Callees { get; set; }
    }

    public class ContextResult
    {
        [JsonPropertyName("target_function")]
        public TargetFunction TargetFunction { get; set; }

        [JsonPropertyName("dependencies")]
        public ContextDependencies Dependencies { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }
    }

    // =========================================================================
    // ASTExtractor — primary entry point per CodeWeave.md spec
    // =========================================================================

    public class ASTExtractor
    {
        // UnsafeRelaxedJsonEscaping prevents C++ symbols (<, >, &, quotes) from
        // being bloated into \uXXXX sequences in the JSON output.
        static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
#if DEBUG
            WriteIndented = true,
#else
            WriteIndented = false,
#endif
        };

        // Primitive type names that are not meaningful callee dependencies
        static readonly HashSet<string> PrimitiveTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "void", "int", "char", "float", "double", "bool", "long", "short",
            "unsigned", "signed", "auto", "wchar_t", "size_t", "ptrdiff_t",
            "uint8_t", "int8_t", "uint16_t", "int16_t", "uint32_t", "int32_t",
            "uint64_t", "int64_t", "string", "var", "object", "dynamic",
        };

        /// <summary>
        /// Extract semantic context JSON for the function containing the given position.
        /// targetLine and targetColumn are 1-based (as provided by Visual Studio SDK).
        /// </summary>
        public string ExtractContextJson(string filePath, string fileContent, int targetLine, int targetColumn)
        {
            try
            {
                string tsLang = GetTreeSitterLang(filePath);
                if (tsLang == "Unknown")
                    return Err("Unsupported file type");

                string lang = GetLang(filePath);

                // Block-scoped using statements ensure unmanaged TreeSitter resources are disposed
                using (Language language = new Language(tsLang))
                using (Parser parser = new Parser(language))
                {
                    Tree tree = parser.Parse(fileContent);
                    if (tree == null)
                        throw new InvalidOperationException("TreeSitter failed to parse file");

                    using (tree)
                    {
                        // Convert 1-based VS coordinates to 0-based TreeSitter rows/columns
                        int row = targetLine - 1;
                        int col = targetColumn - 1;

                        // Walk the AST to build an ancestor path to the deepest node at (row, col)
                        List<Node> path = new List<Node>();
                        if (!FindPath(tree.RootNode, row, col, path) || path.Count == 0)
                            return Err("No AST node found at specified coordinates");

                        // Ascend the path to find the enclosing function definition
                        Node funcNode = null;
                        int funcIdx = -1;
                        for (int i = path.Count - 1; i >= 0; i--)
                        {
                            if (IsFunctionNode(path[i].Type))
                            {
                                funcNode = path[i];
                                funcIdx = i;
                                break;
                            }
                        }

                        if (funcNode == null)
                            return Err("Function not found at specified coordinates");

                        // Extract function properties
                        string simpleName = ExtractFuncSimpleName(funcNode, lang);
                        string qualifiedName = BuildQualifiedName(path, funcIdx, lang, simpleName);
                        string signature = BuildSignature(funcNode, lang, simpleName);
                        Node funcParent = funcIdx > 0 ? path[funcIdx - 1] : null;
                        string docComment = FindDocComment(funcNode, funcParent);
                        Node bodyNode = FindBody(funcNode);

                        // Single-pass file scan to collect type definitions for cross-referencing
                        Dictionary<string, string> typeDefinitions = CollectTypeDefinitions(tree.RootNode, lang);

                        // Collect callees from the function body (call_expression nodes)
                        List<CalleeDependency> callees = new List<CalleeDependency>();
                        if (bodyNode != null)
                            CollectCallees(bodyNode, callees);

                        // Collect referenced types from parameters and local declarations
                        HashSet<string> seenTypes = new HashSet<string>(StringComparer.Ordinal);
                        List<TypeDependency> types = new List<TypeDependency>();
                        CollectTypesFromParams(funcNode, lang, types, seenTypes, typeDefinitions);
                        if (bodyNode != null)
                            CollectTypesFromBody(bodyNode, lang, types, seenTypes, typeDefinitions);

                        ContextResult result = new ContextResult
                        {
                            TargetFunction = new TargetFunction
                            {
                                Name = qualifiedName,
                                Signature = signature,
                                DocComment = docComment,
                                SourceRange = new SourceRange
                                {
                                    StartLine = funcNode.StartPosition.Row + 1,
                                    EndLine = funcNode.EndPosition.Row + 1,
                                },
                                // Use node.Text — TreeSitter slices the raw source via character positions
                                BodyCode = funcNode.Text,
                            },
                            Dependencies = new ContextDependencies
                            {
                                Types = types.Count > 0 ? types : null,
                                Callees = callees.Count > 0 ? callees : null,
                            },
                        };

                        return JsonSerializer.Serialize(result, SerializerOptions);
                    }
                }
            }
            catch (Exception ex)
            {
                return Err(ex.Message);
            }
        }

        // =====================================================================
        // AST traversal
        // =====================================================================

        /// <summary>
        /// Depth-first search that fills <paramref name="path"/> with the chain of nodes
        /// from the root down to (and including) the deepest node containing (row, col).
        /// </summary>
        static bool FindPath(Node node, int row, int col, List<Node> path)
        {
            if (!Contains(node, row, col))
                return false;

            path.Add(node);

            foreach (Node child in node.Children)
            {
                if (FindPath(child, row, col, path))
                    return true;
            }

            // No child contained the position — this node is the deepest match
            return true;
        }

        static bool Contains(Node node, int row, int col)
        {
            int sr = node.StartPosition.Row;
            int sc = node.StartPosition.Column;
            int er = node.EndPosition.Row;
            int ec = node.EndPosition.Column;
            if (row < sr || row > er) return false;
            if (row == sr && col < sc) return false;
            if (row == er && col > ec) return false;
            return true;
        }

        static bool IsFunctionNode(string type)
        {
            return type == "function_definition"           // C/C++ definition with body
                || type == "declaration"                   // C/C++ (less common target)
                || type == "method_declaration"            // C#
                || type == "constructor_declaration"       // C#
                || type == "destructor_declaration"        // C#
                || type == "operator_declaration"          // C#
                || type == "conversion_operator_declaration"
                || type == "local_function_statement"      // C# local functions
                || type == "function_item";                // Rust
        }

        // =====================================================================
        // Name extraction
        // =====================================================================

        static string ExtractFuncSimpleName(Node funcNode, string lang)
        {
            if (lang == "C" || lang == "Cpp")
            {
                // C++: function_definition → function_declarator → [qualified_]identifier
                string name = FindCppFuncName(funcNode);
                if (!string.IsNullOrEmpty(name)) return name;
            }

            // Grammar field "name" is reliable for C# and most grammars
            foreach (var kv in funcNode.Fields)
                if (kv.Key == "name") return kv.Value.Text;

            // Fallback: first identifier child
            foreach (Node child in funcNode.Children)
            {
                if (child.Type == "identifier" || child.Type == "property_identifier")
                    return child.Text;
            }

            return "";
        }

        // Walk declarator wrappers to reach the function_declarator
        static string FindCppFuncName(Node node)
        {
            foreach (Node child in node.Children)
            {
                if (child.Type == "function_declarator")
                    return ExtractCppDeclName(child);
                if (child.Type == "pointer_declarator"
                    || child.Type == "reference_declarator"
                    || child.Type == "rvalue_reference_declarator")
                {
                    string n = FindCppFuncName(child);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            return "";
        }

        // The first non-param child of function_declarator is the callable name
        static string ExtractCppDeclName(Node funcDecl)
        {
            foreach (Node dc in funcDecl.Children)
            {
                if (dc.Type == "identifier"
                    || dc.Type == "qualified_identifier"
                    || dc.Type == "scoped_identifier"
                    || dc.Type == "destructor_name"
                    || dc.Type == "operator_name")
                    return dc.Text;
                if (dc.Type == "pointer_declarator" || dc.Type == "reference_declarator")
                {
                    string n = FindCppFuncName(dc);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            return "";
        }

        /// <summary>
        /// Build a fully qualified name (e.g. Namespace::Class::Method or Ns.Class.Method).
        /// C++ out-of-class definitions already embed the qualifier in simpleName.
        /// </summary>
        static string BuildQualifiedName(List<Node> path, int funcIdx, string lang, string simpleName)
        {
            if ((lang == "C" || lang == "Cpp") && simpleName.Contains("::"))
                return simpleName;

            string sep = (lang == "C" || lang == "Cpp") ? "::" : ".";
            List<string> parts = new List<string>();

            for (int i = 0; i < funcIdx; i++)
            {
                string t = path[i].Type;
                if (t == "namespace_definition"
                    || t == "namespace_declaration"
                    || t == "file_scoped_namespace_declaration"
                    || t == "class_specifier"
                    || t == "struct_specifier"
                    || t == "union_specifier"
                    || t == "class_declaration"
                    || t == "struct_declaration"
                    || t == "interface_declaration"
                    || t == "record_declaration")
                {
                    string name = GetNodeSimpleName(path[i]);
                    if (!string.IsNullOrEmpty(name))
                        parts.Add(name);
                }
            }

            parts.Add(simpleName);
            return string.Join(sep, parts);
        }

        static string GetNodeSimpleName(Node node)
        {
            foreach (var kv in node.Fields)
                if (kv.Key == "name") return kv.Value.Text;
            foreach (Node child in node.Children)
            {
                if (child.Type == "identifier"
                    || child.Type == "type_identifier"
                    || child.Type == "namespace_name")
                    return child.Text;
            }
            return "";
        }

        // =====================================================================
        // Signature building
        // =====================================================================

        static string BuildSignature(Node funcNode, string lang, string simpleName)
        {
            // Strip qualification to get just the method name for the signature
            string lastName;
            int colonIdx = simpleName.LastIndexOf("::", StringComparison.Ordinal);
            if (colonIdx >= 0)
                lastName = simpleName.Substring(colonIdx + 2);
            else
            {
                int dotIdx = simpleName.LastIndexOf('.');
                lastName = dotIdx >= 0 ? simpleName.Substring(dotIdx + 1) : simpleName;
            }

            bool isCtor = funcNode.Type.Contains("constructor");
            bool isDtor = funcNode.Type.Contains("destructor")
                       || ((lang == "C" || lang == "Cpp") && lastName.StartsWith("~"));

            StringBuilder sb = new StringBuilder();

            if (!isCtor && !isDtor)
            {
                string rt = ExtractReturnType(funcNode, lang);
                if (!string.IsNullOrEmpty(rt))
                {
                    sb.Append(rt);
                    sb.Append(' ');
                }
            }

            sb.Append(lastName);

            // Use raw parameter list text from source to preserve types and names exactly
            string pText = ExtractRawParamsText(funcNode);
            sb.Append(pText.Length > 0 ? pText : "()");

            if (lang == "C" || lang == "Cpp")
            {
                if (HasCppQualifier(funcNode, "const")) sb.Append(" const");
                if (HasCppNoexcept(funcNode)) sb.Append(" noexcept");
            }

            return sb.ToString();
        }

        static string ExtractReturnType(Node funcNode, string lang)
        {
            if (lang == "C" || lang == "Cpp")
            {
                List<string> prefixes = new List<string>();
                string baseType = null;
                string suffix = "";

                foreach (Node child in funcNode.Children)
                {
                    switch (child.Type)
                    {
                        case "type_qualifier":
                            prefixes.Add(child.Text);
                            break;
                        case "type_identifier":
                        case "primitive_type":
                        case "scoped_type_identifier":
                        case "template_type":
                        case "auto":
                            if (baseType == null) baseType = child.Text;
                            break;
                        case "pointer_declarator":
                        case "abstract_pointer_declarator":
                            suffix = "*";
                            break;
                        case "reference_declarator":
                        case "abstract_reference_declarator":
                            suffix = child.Text.Contains("&&") ? "&&" : "&";
                            break;
                        case "function_declarator":
                        case "compound_statement":
                            goto done;
                    }
                }
                done:
                if (baseType != null)
                {
                    string prefix = prefixes.Count > 0 ? string.Join(" ", prefixes) + " " : "";
                    return prefix + baseType + suffix;
                }
                return null;
            }

            // C# and other languages: use grammar field "type" or "return_type"
            foreach (var kv in funcNode.Fields)
            {
                if (kv.Key == "type" || kv.Key == "return_type")
                    return kv.Value.Text;
            }

            return null;
        }

        // Returns the raw parameter list text including parentheses, e.g. "(int x, float y)"
        static string ExtractRawParamsText(Node funcNode)
        {
            Node pl = FindParamList(funcNode);
            if (pl == null) return "()";

            string text = pl.Text;
            if (!text.StartsWith("(")) text = "(" + text;
            if (!text.EndsWith(")")) text = text + ")";
            return text;
        }

        static Node FindParamList(Node node)
        {
            foreach (Node child in node.Children)
            {
                if (child.Type == "parameter_list"
                    || child.Type == "formal_parameters"
                    || child.Type == "parameters")
                    return child;

                // Recurse through C++ declarator wrappers
                if (child.Type == "function_declarator"
                    || child.Type == "pointer_declarator"
                    || child.Type == "reference_declarator"
                    || child.Type == "rvalue_reference_declarator")
                {
                    Node found = FindParamList(child);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static bool HasCppQualifier(Node funcNode, string keyword)
        {
            foreach (Node child in funcNode.Children)
            {
                if (child.Type == "function_declarator")
                {
                    foreach (Node dc in child.Children)
                    {
                        if (dc.Type == "type_qualifier" && dc.Text == keyword)
                            return true;
                    }
                }
            }
            return false;
        }

        static bool HasCppNoexcept(Node funcNode)
        {
            foreach (Node child in funcNode.Children)
            {
                if (child.Type == "function_declarator")
                {
                    foreach (Node dc in child.Children)
                    {
                        if (dc.Type == "noexcept")
                            return true;
                    }
                }
            }
            return false;
        }

        // =====================================================================
        // Doc comment — find the immediately preceding comment sibling
        // =====================================================================

        static string FindDocComment(Node funcNode, Node funcParent)
        {
            if (funcParent == null) return null;

            Node last = null;
            foreach (Node child in funcParent.Children)
            {
                // Stop when we reach funcNode itself (identified by start position)
                if (child.StartPosition.Row == funcNode.StartPosition.Row
                    && child.StartPosition.Column == funcNode.StartPosition.Column)
                    break;

                if (child.Type == "comment")
                    last = child;   // keep updating; the closest one wins
                else
                    last = null;    // reset — a non-comment breaks the chain
            }

            return last != null ? last.Text : null;
        }

        // =====================================================================
        // Body node
        // =====================================================================

        static Node FindBody(Node funcNode)
        {
            foreach (Node child in funcNode.Children)
            {
                if (child.Type == "compound_statement"
                    || child.Type == "block"
                    || child.Type == "statement_block")
                    return child;
            }
            return null;
        }

        // =====================================================================
        // Callee extraction — walks body for call_expression / invocation_expression
        // =====================================================================

        static void CollectCallees(Node body, List<CalleeDependency> callees)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            CollectCalleesWalk(body, callees, seen);
        }

        static void CollectCalleesWalk(Node node, List<CalleeDependency> callees, HashSet<string> seen)
        {
            if (node.Type == "call_expression" || node.Type == "invocation_expression")
            {
                string name = ResolveCallName(node);
                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                {
                    callees.Add(new CalleeDependency
                    {
                        Name = name,
                        Reason = "Called inside target_function",
                    });
                }

                // Walk only arguments to avoid double-counting the callee expression
                foreach (Node child in node.Children)
                {
                    if (child.Type == "argument_list" || child.Type == "arguments")
                        CollectCalleesWalk(child, callees, seen);
                }
                return;
            }

            foreach (Node child in node.Children)
                CollectCalleesWalk(child, callees, seen);
        }

        static string ResolveCallName(Node callNode)
        {
            // The callee is the first child of the call expression node
            bool first = true;
            foreach (Node child in callNode.Children)
            {
                if (!first) break;
                first = false;

                if (child.Type == "identifier")
                    return child.Text;

                // Member or qualified calls: return full text (e.g. "obj->method" or "Ns::func")
                if (child.Type == "member_access_expression"
                    || child.Type == "field_expression"
                    || child.Type == "pointer_field_expression"
                    || child.Type == "qualified_identifier"
                    || child.Type == "scoped_identifier")
                    return child.Text;
            }
            return "";
        }

        // =====================================================================
        // Type dependency extraction
        // =====================================================================

        /// <summary>
        /// Single-pass walk collecting all struct/class/enum definitions in the file
        /// so their source text can be embedded in the dependency output.
        /// </summary>
        static Dictionary<string, string> CollectTypeDefinitions(Node root, string lang)
        {
            Dictionary<string, string> defs = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectTypeDefsWalk(root, lang, defs);
            return defs;
        }

        static void CollectTypeDefsWalk(Node node, string lang, Dictionary<string, string> defs)
        {
            if (IsTypeDefinitionNode(lang, node.Type))
            {
                string name = GetNodeSimpleName(node);
                if (!string.IsNullOrEmpty(name) && !defs.ContainsKey(name))
                    defs[name] = node.Text;
            }
            foreach (Node child in node.Children)
                CollectTypeDefsWalk(child, lang, defs);
        }

        static bool IsTypeDefinitionNode(string lang, string type)
        {
            if (lang == "C" || lang == "Cpp")
            {
                return type == "class_specifier"
                    || type == "struct_specifier"
                    || type == "union_specifier"
                    || type == "enum_specifier";
            }
            if (lang == "CSharp")
            {
                return type == "class_declaration"
                    || type == "struct_declaration"
                    || type == "interface_declaration"
                    || type == "enum_declaration"
                    || type == "record_declaration"
                    || type == "record_struct_declaration";
            }
            return false;
        }

        static void CollectTypesFromParams(Node funcNode, string lang,
                                           List<TypeDependency> types,
                                           HashSet<string> seen,
                                           Dictionary<string, string> typeDefs)
        {
            Node paramList = FindParamList(funcNode);
            if (paramList == null) return;

            foreach (Node param in paramList.Children)
            {
                if (param.Type != "parameter"
                    && param.Type != "required_parameter"
                    && param.Type != "optional_parameter"
                    && param.Type != "variadic_parameter"
                    && param.Type != "parameter_declaration")
                    continue;

                string typeName = ExtractTypeNameFromNode(param);
                AddTypeDependency(typeName, "Used as parameter type", types, seen, typeDefs);
            }
        }

        static void CollectTypesFromBody(Node body, string lang,
                                         List<TypeDependency> types,
                                         HashSet<string> seen,
                                         Dictionary<string, string> typeDefs)
        {
            CollectBodyTypesWalk(body, types, seen, typeDefs);
        }

        static void CollectBodyTypesWalk(Node node,
                                          List<TypeDependency> types,
                                          HashSet<string> seen,
                                          Dictionary<string, string> typeDefs)
        {
            // C/C++ local declaration
            if (node.Type == "declaration" || node.Type == "init_declarator")
            {
                string typeName = ExtractTypeNameFromNode(node);
                AddTypeDependency(typeName, "Used as local variable type", types, seen, typeDefs);
            }
            // C# local declaration
            else if (node.Type == "local_declaration_statement" || node.Type == "variable_declaration")
            {
                string typeName = ExtractTypeNameFromNode(node);
                AddTypeDependency(typeName, "Used as local variable type", types, seen, typeDefs);
            }
            // Object construction — new Foo(...)
            else if (node.Type == "object_creation_expression"
                  || node.Type == "new_expression"
                  || node.Type == "implicit_object_creation_expression")
            {
                foreach (Node child in node.Children)
                {
                    if (child.Type == "type_identifier"
                        || child.Type == "identifier"
                        || child.Type == "qualified_name"
                        || child.Type == "generic_name"
                        || child.Type == "scoped_type_identifier")
                    {
                        AddTypeDependency(child.Text, "Instantiated inside target_function",
                                          types, seen, typeDefs);
                        break;
                    }
                }
            }

            foreach (Node child in node.Children)
                CollectBodyTypesWalk(child, types, seen, typeDefs);
        }

        static string ExtractTypeNameFromNode(Node node)
        {
            bool skipQualifiers = false;
            foreach (Node child in node.Children)
            {
                if (child.Type == "type_qualifier")
                {
                    skipQualifiers = true;
                    continue;
                }
                if (child.Type == "type_identifier"
                    || child.Type == "scoped_type_identifier"
                    || child.Type == "template_type"
                    || child.Type == "generic_name"
                    || child.Type == "qualified_name"
                    || child.Type == "predefined_type"
                    || child.Type == "primitive_type")
                    return child.Text;
            }
            return "";
        }

        static void AddTypeDependency(string typeName, string reason,
                                      List<TypeDependency> types,
                                      HashSet<string> seen,
                                      Dictionary<string, string> typeDefs)
        {
            if (string.IsNullOrEmpty(typeName)) return;

            // Strip template arguments for lookup: "vector<int>" → "vector"
            string baseName = typeName;
            int angleIdx = typeName.IndexOf('<');
            if (angleIdx > 0)
                baseName = typeName.Substring(0, angleIdx);

            if (PrimitiveTypes.Contains(baseName)) return;
            if (!seen.Add(baseName)) return;

            string definition;
            typeDefs.TryGetValue(baseName, out definition); // null if not found in this file

            types.Add(new TypeDependency
            {
                Name = typeName,
                Definition = definition,
                Reason = reason,
            });
        }

        // =====================================================================
        // Language helpers
        // =====================================================================

        static string GetTreeSitterLang(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".c") return "C";
            if (ext == ".cpp" || ext == ".cc" || ext == ".h" || ext == ".hpp" || ext == ".cxx") return "Cpp";
            if (ext == ".cs") return "C-Sharp";
            return "Unknown";
        }

        static string GetLang(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".c") return "C";
            if (ext == ".cpp" || ext == ".cc" || ext == ".h" || ext == ".hpp" || ext == ".cxx") return "Cpp";
            if (ext == ".cs") return "CSharp";
            return "Unknown";
        }

        static string Err(string message)
        {
            return JsonSerializer.Serialize(new ContextResult { Error = message }, SerializerOptions);
        }
    }
}
