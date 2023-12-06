﻿using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Newtonsoft.Json;
using ProtoBuf;
using System.IO;
using System.Linq;
using CodeiumVS.Packets;
using WebSocketSharp;

namespace CodeiumVS;


public class LanguageServerController
{
    readonly CodeiumVSPackage Package;
    public WebSocket? ws = null;
    public LanguageServerController()
    {
        Package = CodeiumVSPackage.Instance;
    }
    
    public async Task ConnectAsync()
    {

#pragma warning disable VSTHRD103 // Call async methods when in an async method
        void OnOpen(object sender, EventArgs e)
        {
            WebSocket ws = sender as WebSocket;
            Package.Log($"Connected to {ws.Url}");
        }

        void OnClose(object sender, EventArgs e)
        {
            WebSocket ws = sender as WebSocket;
            Package.Log($"Disconnected from {ws.Url}");
        }

        void OnMessage(object sender, EventArgs e)
        {
            MessageEventArgs msg = e as MessageEventArgs;
            if (!msg.IsBinary) return;

            using MemoryStream stream = new(msg.RawData);
            WebServerResponse request = Serializer.Deserialize<WebServerResponse>(stream);
            string text = JsonConvert.SerializeObject(request);

            Package.Log($"OnMessage: {text}");

            if (request.ShouldSerializeopen_file_pointer())
            {
                var data = request.open_file_pointer;
                OpenSelection(data.file_path, data.start_line, data.start_col, data.end_line, data.end_col);
            }
            else if (request.ShouldSerializeinsert_at_cursor())
            {
                var data = request.insert_at_cursor;
                InsertText(data.text);
            }

        }

        void OnError(object sender, EventArgs e)
        {
            Package.Log("OnError\n");
        }
#pragma warning restore VSTHRD103 // Call async methods when in an async method

        GetProcessesResponse? result = await Package.LanguageServer.GetProcessesAsync();

        ws = new WebSocket($"ws://127.0.0.1:{result.chatWebServerPort}/connect/ide");
        ws.OnOpen    += OnOpen;
        ws.OnClose   += OnClose;
        ws.OnMessage += OnMessage;
        ws.OnError   += OnError;
        ws.ConnectAsync();
    }

    private void InsertText(string text)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextView == null) return; //not a text window

            ITextSelection selection = docView.TextView.Selection;

            // has no selection
            if (selection.SelectedSpans.Count == 0 || selection.SelectedSpans[0].Span.Start == selection.SelectedSpans[0].Span.End)
            {
                // Inserts text at the caret
                SnapshotPoint position = docView.TextView.Caret.Position.BufferPosition;
                docView.TextBuffer?.Insert(position, text);
            }
            else
            {
                // Inserts text in the selection
                docView.TextBuffer?.Replace(selection.SelectedSpans[0].Span, text);
            }
        }).FireAndForget(true);
    }

    private void OpenSelection(string filePath, int start_line, int start_col, int end_line, int end_col)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // some how OpenViaProjectAsync doesn't work... at least for me
            DocumentView? docView = await VS.Documents.OpenAsync(filePath);
            if (docView?.TextView == null) return;

            var snapshot = docView.TextView.TextSnapshot;
            ITextSelection selection = docView.TextView.Selection;

            var lineStart = docView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(start_line - 1);
            var lineEnd = docView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(end_line - 1);

            int start = lineStart.Start.Position + start_col - 1;
            int end = lineEnd.Start.Position + end_col - 1;

            docView.TextView.Selection.Select(new SnapshotSpan(snapshot, start, end - start), false);
            docView.TextView.Caret.MoveTo(new SnapshotPoint(snapshot, end));
            docView.TextView.Caret.EnsureVisible();
        }).FireAndForget();
    }


    public async Task ExplainCodeBlockAsync(string filePath, Language language, CodeBlockInfo codeBlockInfo)
    {
        var request = WebChatServer.NewRequest();
        request.get_chat_message_request.chat_messages[0].intent = new()
        {
            explain_code_block = new()
            {
                code_block_info = codeBlockInfo,
                file_path = filePath,
                language = language,
            }
        };

        request.Send(ws);
        await Package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, Package.DisposalToken);
    }

    public async Task ExplainFunctionAsync(string filePath, FunctionInfo functionInfo)
    {
        var request = WebChatServer.NewRequest();
        request.get_chat_message_request.chat_messages[0].intent = new()
        {
            explain_function = new()
            {
                function_info = functionInfo,
                file_path = filePath,
                language = functionInfo.language,
            }
        };

        request.Send(ws);
        await Package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, Package.DisposalToken);
    }
    
    public async Task GenerateFunctionUnitTestAsync(string instructions, string filePath, FunctionInfo functionInfo)
    {
        var request = WebChatServer.NewRequest();
        request.get_chat_message_request.chat_messages[0].intent = new()
        {
            function_unit_tests = new()
            {
                function_info = functionInfo,
                file_path = filePath,
                language = functionInfo.language,
                instructions = instructions,
            }
        };

        request.Send(ws);
        await Package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, Package.DisposalToken);
    }

    public async Task GenerateFunctionDocstringAsync(string filePath, FunctionInfo functionInfo)
    {
        var request = WebChatServer.NewRequest();
        request.get_chat_message_request.chat_messages[0].intent = new()
        {
            function_docstring = new()
            {
                function_info = functionInfo,
                file_path = filePath,
                language = functionInfo.language,
            }
        };

        request.Send(ws);
        await Package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, Package.DisposalToken);
    }

    public async Task RefactorCodeBlockAsync(string prompt, string filePath, Language language, CodeBlockInfo codeBlockInfo)
    {
        var request = WebChatServer.NewRequest();
        request.get_chat_message_request.chat_messages[0].intent = new()
        {
            code_block_refactor = new()
            {
                code_block_info = codeBlockInfo,
                file_path = filePath,
                language = language,
                refactor_description = prompt
            }
        };

        request.Send(ws);
        await Package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, Package.DisposalToken);
    }

    public async Task RefactorFunctionAsync(string prompt, string filePath, FunctionInfo functionInfo)
    {
        var request = WebChatServer.NewRequest();
        request.get_chat_message_request.chat_messages[0].intent = new()
        {
            function_refactor = new()
            {
                function_info = functionInfo,
                file_path = filePath,
                language = functionInfo.language,
                refactor_description = prompt
            }
        };

        request.Send(ws);
        await Package.ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, Package.DisposalToken);
    }

    public void Disconnect()
    {
        if (ws == null) return;
        ws.Close();
        ws = null;
    }
}

internal static class WebChatServer
{
    internal static WebServerRequest NewRequest()
    {
        static string RandomString(int length)
        {
            Random random = new();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            return new string(Enumerable.Repeat(chars, length)
              .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        WebServerRequest request = new()
        {
            get_chat_message_request = new()
            {
                context_inclusion_type = Packets.ContextInclusionType.CONTEXT_INCLUSION_TYPE_UNSPECIFIED,
                metadata = CodeiumVSPackage.Instance?.LanguageServer.GetMetadata(),
                prompt = ""
            }
        };

        request.get_chat_message_request.chat_messages.Add(new()
        {
            conversation_id = RandomString(32),
            in_progress = false,
            message_id = $"user-{RandomString(32)}",
            source = ChatMessageSource.CHAT_MESSAGE_SOURCE_USER,
            timestamp = DateTime.UtcNow
        });

        return request;
    }

    internal static void Send(this WebServerRequest request, WebSocket ws)
    {
        using MemoryStream memoryStream = new();
        Serializer.Serialize(memoryStream, request);
        ws.SendAsync(memoryStream.ToArray(), delegate (bool e) { });
    }
}