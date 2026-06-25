using Betalgo.Ranul.OpenAI.Interfaces;
using Betalgo.Ranul.OpenAI.Managers;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent
{
    public class Agent : IDisposable
    {
        public record QueueItem(string message, Action<string,string,string> onProgress, Action<string> onError);

        private readonly BlockingCollection<QueueItem> queue_ = new();
        private readonly HttpClient httpClient_;
        private readonly CancellationTokenSource cancellationTokenSource_ = new();
        private Task task_;
        private bool disposed_;
        private volatile bool isRunning_;
        private OpenAIService openAIService_;

        public bool IsRunning => isRunning_;

        public Agent()
        {
            Betalgo.Ranul.OpenAI.OpenAIOptions openAIOptions = new Betalgo.Ranul.OpenAI.OpenAIOptions()
            {
                BaseDomain = "http://localhost:9090"
            };
            openAIService_ = new OpenAIService(openAIOptions);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed_)
            {
                return;
            }

            if (disposing)
            {
                isRunning_ = false;
                cancellationTokenSource_.Cancel();
                queue_.CompleteAdding();

                cancellationTokenSource_.Dispose();
                queue_.Dispose();
            }

            disposed_ = true;
        }

        public void Start()
        {
            if (disposed_)
            {
                throw new ObjectDisposedException(nameof(Agent));
            }
            if (task_ != null)
            {
                return;
            }
            isRunning_ = true;
            task_ = Task.Factory.StartNew(
                () => ProcessQueueAsync(cancellationTokenSource_.Token),
                cancellationTokenSource_.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        public void Enqueue(string url, Action<string,string,string> progressCallback, Action<string> errorCallback)
        {
            if (disposed_)
            {
                throw new ObjectDisposedException(nameof(Agent));
            }
            if (queue_.IsAddingCompleted)
            {
                throw new InvalidOperationException("Queue is stopping.");
            }
            queue_.Add(new QueueItem(url, progressCallback, errorCallback));
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                foreach (QueueItem item in queue_.GetConsumingEnumerable(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        List<ChatMessage> messages = new List<ChatMessage>{
                            ChatMessage.FromSystem("You are a helpful assistant."),
                            ChatMessage.FromUser("Who won the world series in 2020?"),
                            ChatMessage.FromAssistant("The Los Angeles Dodgers won the World Series in 2020."),
                            ChatMessage.FromUser("Where was it played?")
                        };
                        ChatCompletionCreateRequest chatCompletionCreateRequest = new ChatCompletionCreateRequest()
                        {
                            Model = "gpt-3.5-turbo",
                            Messages = messages,
                            MaxTokens = 2048,
                            Temperature = 0.7f
                        };
                        await foreach(ChatCompletionCreateResponse update in openAIService_.ChatCompletion.CreateCompletionAsStream(chatCompletionCreateRequest))
                        {
                            foreach(ChatChoiceResponse choice in update.Choices) {
                                item.onProgress?.Invoke(update.Id, choice.Message.Role, choice.Message.Content);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        item.onError?.Invoke("[中断] タスク全体がキャンセルされました。");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        item.onError?.Invoke($"[エラー] 処理失敗: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                isRunning_ = false;
            }
        }

        public async Task StopAsync()
        {
            if (disposed_)
            {
                return;
            }

            queue_.CompleteAdding();
            cancellationTokenSource_.Cancel();

            try
            {
                if (task_ != null)
                {
                    await task_;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                isRunning_ = false;
            }
        }
    }
}
