import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';

interface Msg {
  role: 'user' | 'bot';
  text: string;
}

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="chat">
      <div class="head">Підтримка</div>
      <div class="body">
        <div *ngFor="let m of messages" class="msg" [class.user]="m.role === 'user'">
          {{ m.text }}
        </div>
      </div>
      <form class="input" (submit)="send($event)">
        <input [(ngModel)]="draft" name="draft" placeholder="Напишіть повідомлення…" autocomplete="off" />
        <button type="submit">➤</button>
      </form>
    </div>
  `,
  styles: [
    `
      .chat { border: 1px solid #e2e2e2; border-radius: 12px; overflow: hidden; background: #fff; }
      .head { padding: 12px 16px; background: #f6f6f8; font-weight: 600; border-bottom: 1px solid #eee; }
      .body { padding: 16px; display: flex; flex-direction: column; gap: 8px; min-height: 240px; }
      .msg { max-width: 80%; padding: 8px 12px; border-radius: 12px; background: #f1f1f4; align-self: flex-start; }
      .msg.user { align-self: flex-end; background: #4038c4; color: #fff; }
      .input { display: flex; gap: 8px; padding: 12px; border-top: 1px solid #eee; }
      .input input { flex: 1; padding: 8px 12px; border: 1px solid #ddd; border-radius: 20px; }
      .input button { border: none; background: #4038c4; color: #fff; border-radius: 50%; width: 36px; height: 36px; cursor: pointer; }
    `,
  ],
})
export class ChatComponent {
  messages: Msg[] = [{ role: 'bot', text: 'Вітаю! Чим можу допомогти?' }];
  draft = '';

  constructor(private http: HttpClient) {}

  send(e: Event): void {
    e.preventDefault();
    const text = this.draft.trim();
    if (!text) return;
    this.messages.push({ role: 'user', text });
    this.draft = '';
    this.http.post<{ content: string }>('/api/chat', { message: text }).subscribe({
      next: (r) => this.messages.push({ role: 'bot', text: r.content }),
      error: () => this.messages.push({ role: 'bot', text: 'Помилка зʼєднання.' }),
    });
  }
}
