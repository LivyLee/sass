﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace sass
{
    public class Assembler
    {
        public InstructionSet InstructionSet { get; set; }
        public ExpressionEngine ExpressionEngine { get; set; }
        public AssemblyOutput Output { get; set; }
        public Encoding Encoding { get; set; }
        public List<string> IncludePaths { get; set; }

        private uint PC { get; set; }
        private string[] Lines { get; set; }
        private int RootLineNumber { get; set; }
        private Stack<int> LineNumbers { get; set; }
        private Stack<string> FileNames { get; set; }
        private Stack<bool> IfStack { get; set; }
        private int SuspendedLines { get; set; }
        private int CurrentIndex { get; set; }

        public Assembler(InstructionSet instructionSet)
        {
            InstructionSet = instructionSet;
            ExpressionEngine = new ExpressionEngine();
            SuspendedLines = 0;
            LineNumbers = new Stack<int>();
            FileNames = new Stack<string>();
            IncludePaths = new List<string>();
        }

        public AssemblyOutput Assemble(string assembly, string fileName = null)
        {
            var output = new AssemblyOutput();
            assembly = assembly.Replace("\r", "");
            PC = 0;
            Lines = assembly.Split('\n');
            FileNames.Push(fileName);
            LineNumbers.Push(0);
            RootLineNumber = 0;
            for (CurrentIndex = 0; CurrentIndex < Lines.Length; CurrentIndex++)
            {
                string line = Lines[CurrentIndex].Trim().TrimComments();
                if (SuspendedLines == 0)
                {
                    LineNumbers.Push(LineNumbers.Pop() + 1);
                    RootLineNumber++;
                }
                else
                    SuspendedLines--;

                if (line.SafeContains('\\'))
                {
                    // Split lines up
                    var split = line.SafeSplit('\\');
                    Lines = Lines.Take(CurrentIndex).Concat(split).
                        Concat(Lines.Skip(CurrentIndex + 1)).ToArray();
                    SuspendedLines = split.Length;
                    CurrentIndex--;
                    continue;
                }

                if (line.StartsWith(".") || line.StartsWith("#")) // Directive
                {
                    var result = HandleDirective(line);
                    if (result != null)
                        output.Listing.Add(result);
                    continue;
                }
                else if (line.StartsWith(":") || line.EndsWith(":")) // Label
                {
                    string label;
                    if (line.StartsWith(":"))
                        label = line.Substring(1).Trim();
                    else
                        label = line.Remove(line.Length - 1).Trim();
                    label = label.ToLower();
                    bool valid = true;
                    for (int k = 0; k < label.Length; k++) // Validate label
                    {
                        if (!char.IsLetterOrDigit(label[k]) && k != '_')
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (!valid)
                    {
                        output.Listing.Add(new Listing
                        {
                            Code = line,
                            CodeType = CodeType.Label,
                            Error = AssemblyError.InvalidLabel,
                            Warning = AssemblyWarning.None,
                            Address = PC,
                            FileName = FileNames.Peek(),
                            LineNumber = LineNumbers.Peek(),
                            RootLineNumber = RootLineNumber
                        });
                    }
                    output.Listing.Add(new Listing
                    {
                        Code = line,
                        CodeType = CodeType.Label,
                        Error = AssemblyError.None,
                        Warning = AssemblyWarning.None,
                        Address = PC,
                        FileName = FileNames.Peek(),
                        LineNumber = LineNumbers.Peek(),
                        RootLineNumber = RootLineNumber
                    });
                    ExpressionEngine.Equates.Add(label, PC);
                }
                else
                {
                    // Check for macro
                    // TODO
                    // Instruction
                    var match = InstructionSet.Match(line);
                    if (match == null)
                    {
                        // Unknown instruction
                        output.Listing.Add(new Listing
                        {
                            Code = line,
                            CodeType = CodeType.Instruction,
                            Error = AssemblyError.InvalidInstruction,
                            Warning = AssemblyWarning.None,
                            Instruction = match,
                            Address = PC,
                            FileName = FileNames.Peek(),
                            LineNumber = LineNumbers.Peek(),
                            RootLineNumber = RootLineNumber
                        });
                    }
                    else
                    {
                        // Instruction to be fully assembled in the next pass
                        output.Listing.Add(new Listing
                        {
                            Code = line,
                            CodeType = CodeType.Instruction,
                            Error = AssemblyError.None,
                            Warning = AssemblyWarning.None,
                            Instruction = match,
                            Address = PC,
                            FileName = FileNames.Peek(),
                            LineNumber = LineNumbers.Peek(),
                            RootLineNumber = RootLineNumber
                        });
                        PC += match.Length;
                    }
                }
            }
            return Finish(output);
        }

        private AssemblyOutput Finish(AssemblyOutput output)
        {
            List<byte> finalBinary = new List<byte>();
            for (int i = 0; i < output.Listing.Count; i++)
            {
                var entry = output.Listing[i];
                RootLineNumber = entry.RootLineNumber;
                PC = entry.Address;
                LineNumbers = new Stack<int>(new[] { entry.LineNumber });
                if (entry.CodeType == CodeType.Directive)
                {
                    if (entry.PostponeEvalulation)
                        output.Listing[i] = HandleDirective(entry.Code, true);
                    if (output.Listing[i].Output != null)
                        finalBinary.AddRange(output.Listing[i].Output);
                    continue;
                }
                if (entry.Error != AssemblyError.None)
                    continue;
                if (entry.CodeType == CodeType.Instruction)
                {
                    // Assemble output string
                    string instruction = entry.Instruction.Value.ToLower();
                    foreach (var operand in entry.Instruction.Operands)
                        instruction = instruction.Replace("@" + operand.Key, operand.Value.Value);
                    foreach (var value in entry.Instruction.ImmediateValues)
                    {
                        // TODO: Truncation warning
                        if (value.Value.RelativeToPC)
                            instruction = instruction.Replace("^" + value.Key, ConvertToBinary(
                                entry.Address -
                                (ExpressionEngine.Evaluate(value.Value.Value, entry.Address) + entry.Instruction.Length),
                                value.Value.Bits));
                        else
                            instruction = instruction.Replace("%" + value.Key, ConvertToBinary(
                                ExpressionEngine.Evaluate(value.Value.Value, entry.Address),
                                value.Value.Bits));
                    }
                    entry.Output = ExpressionEngine.ConvertFromBinary(instruction);
                    finalBinary.AddRange(entry.Output);
                }
            }
            output.Data = finalBinary.ToArray();
            return output;
        }

        private static string ConvertToBinary(ulong value, int bits) // Little endian octets
        {
            ulong mask = 1;
            string result = "";
            for (int i = 0; i < bits; i++)
            {
                if ((value & mask) == mask)
                    result = "1" + result;
                else
                    result = "0" + result;
                mask <<= 1;
            }
            // Convert to little endian
            string little = "";
            for (int i = 0; i < result.Length; i += 8)
                little = result.Substring(i, 8) + little;
            return little;
        }

        #region Directives

        private Listing HandleDirective(string line, bool passTwo = false)
        {
            string directive = line.Substring(1).Trim();
            string[] parameters = new string[0];
            string parameter = "";
            if (directive.SafeContains(' '))
            {
                parameter = directive.Substring(directive.SafeIndexOf(' ') + 1);
                parameters = parameter.SafeSplit(',');
                directive = directive.Remove(directive.SafeIndexOf(' '));
            }
            var listing = new Listing
            {
                Code = line,
                CodeType = CodeType.Directive,
                Address = PC,
                Error = AssemblyError.None,
                Warning = AssemblyWarning.None,
                FileName = FileNames.Peek(),
                LineNumber = LineNumbers.Peek(),
                RootLineNumber = RootLineNumber
            };
            switch (directive)
            {
                case "block":
                {
                    ulong amount = ExpressionEngine.Evaluate(parameter, PC);
                    listing.Output = new byte[amount];
                    PC += (uint)amount;
                    return listing;
                }
                case "byte":
                case "db":
                {
                    if (passTwo)
                    {
                        var result = new List<byte>();
                        foreach (var item in parameters)
                            result.Add((byte)ExpressionEngine.Evaluate(item, PC++));
                        listing.Output = result.ToArray();
                        return listing;
                    }
                    else
                    {
                        listing.Output = new byte[parameters.Length];
                        listing.PostponeEvalulation = true;
                        PC += (uint)listing.Output.Length;
                        return listing;
                    }
                }
                case "word":
                case "dw":
                {
                    if (passTwo)
                    {
                        var result = new List<byte>();
                        foreach (var item in parameters)
                            result.AddRange(TruncateWord(ExpressionEngine.Evaluate(item, PC++)));
                        listing.Output = result.ToArray();
                        return listing;
                    }
                    else
                    {
                        listing.Output = new byte[parameters.Length * (InstructionSet.WordSize / 8)];
                        listing.PostponeEvalulation = true;
                        PC += (uint)listing.Output.Length;
                        return listing;
                    }
                }
                case "error":
                case "echo":
                {
                    string output = "";
                    foreach (var item in parameters)
                    {
                        if (item.Trim().StartsWith("\"") && item.EndsWith("\""))
                            output += item.Substring(1, item.Length - 2);
                        else
                            output += ExpressionEngine.Evaluate(item, PC);
                    }
                    Console.WriteLine((directive == "error" ? "User Error: " : "") + output);
                    return listing;
                }
                case "nolist":
                case "list": // TODO: Do either of these really matter?
                case "end":
                    return listing;
                case "fill":
                {
                    ulong amount = ExpressionEngine.Evaluate(parameters[0], PC);
                    if (parameters.Length == 1)
                    {
                        Array.Resize<string>(ref parameters, 2);
                        parameters[1] = "0";
                    }
                    listing.Output = new byte[amount];
                    for (int i = 0; i < (int)amount; i++)
                        listing.Output[i] = (byte)ExpressionEngine.Evaluate(parameters[1], PC++);
                    return listing;
                }
                case "option": // TODO: Spasm-style bitmap imports
                    return listing;
                case "org":
                    PC = (uint)ExpressionEngine.Evaluate(parameter, PC);
                    return listing;
                case "include":
                    {
                        string file = GetIncludeFile(parameter);
                        if (file == null)
                        {
                            listing.Error = AssemblyError.FileNotFound;
                            return listing;
                        }
                        FileNames.Push(parameter);
                        LineNumbers.Push(0);
                        string includedFile = File.ReadAllText(file) + "\n.endfile";
                        string[] lines = includedFile.Replace("\r", "").Split('\n');
                        Lines = Lines.Take(CurrentIndex + 1).Concat(lines).Concat(Lines.Skip(CurrentIndex + 1)).ToArray();
                        return listing;
                    }
                case "endfile": // Special directive
                    RootLineNumber--;
                    LineNumbers.Pop();
                    FileNames.Pop();
                    return null;
            }
            return null;
        }

        private string GetIncludeFile(string file)
        {
            file = file.Substring(1, file.Length - 2); // Remove <> or ""
            if (File.Exists(file))
                return file;
            foreach (var path in IncludePaths)
            {
                if (File.Exists(Path.Combine(path, file)))
                    return Path.Combine(path, file);
            }
            return null;
        }

        private byte[] TruncateWord(ulong value)
        {
            var array = BitConverter.GetBytes(value);
            return array.Take(InstructionSet.WordSize / 8).ToArray();
        }

        #endregion
    }
}
