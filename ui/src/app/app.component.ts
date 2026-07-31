import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ChatComponent } from './chat.component';
import { ConsoleComponent } from './console.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, ChatComponent, ConsoleComponent],
  template: `
    <div class="wrap">
      <header class="top">SupportGW — LLMOps capstone</header>
      <div class="cols">
        <section class="col"><app-chat></app-chat></section>
        <section class="col"><app-console></app-console></section>
      </div>
    </div>
  `,
  styles: [
    `
      .wrap { max-width: 1180px; margin: 20px auto; padding: 0 16px; }
      .top { font-weight: 700; font-size: 18px; margin-bottom: 16px; }
      .cols { display: grid; grid-template-columns: minmax(0, 0.85fr) minmax(0, 1.15fr); gap: 20px; align-items: start; }
      @media (max-width: 880px) {
        .cols { grid-template-columns: 1fr; }
      }
    `,
  ],
})
export class AppComponent {}
