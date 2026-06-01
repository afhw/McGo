import React, { useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  ArrowDownToLine,
  BadgeCheck,
  Boxes,
  Cable,
  ChevronRight,
  Download,
  Gamepad2,
  HardDriveDownload,
  KeyRound,
  Layers3,
  MonitorDown,
  PackageOpen,
  Play,
  Search,
  Settings2,
  ShieldCheck,
  Sparkles,
  UserRoundCheck,
  Zap,
} from 'lucide-react';
import './styles.css';

const screenshots = [
  {
    src: '/screenshots/01-home.png',
    label: '首页总览',
    title: '账号、Java、版本状态一屏看清',
  },
  {
    src: '/screenshots/02-launch.png',
    label: '启动管理',
    title: '选择账号与版本，快速进入游戏',
  },
  {
    src: '/screenshots/03-download.png',
    label: '游戏下载',
    title: '原版、加载器、Fabric API 连续安装',
  },
  {
    src: '/screenshots/04-resources.png',
    label: '资源市场',
    title: '搜索 Mod、资源包、光影并一键安装',
  },
  {
    src: '/screenshots/05-accounts.png',
    label: '账号体系',
    title: '离线、Microsoft、外置登录统一管理',
  },
  {
    src: '/screenshots/06-online.png',
    label: '联机工具',
    title: '轻量 P2P 隧道，局域网世界也能邀请好友',
  },
];

const features = [
  {
    icon: HardDriveDownload,
    title: '高速下载与补全',
    text: '支持原版版本下载、文件校验、缺失资源补全，下载失败时自动切换镜像源。',
  },
  {
    icon: Layers3,
    title: '加载器连续安装',
    text: '下载后可继续安装 Fabric、Forge、NeoForge、OptiFine 与 Fabric API。',
  },
  {
    icon: UserRoundCheck,
    title: '完整账号管理',
    text: '离线账号、Microsoft 账号、外置登录账号集中保存，启动前自动刷新状态。',
  },
  {
    icon: Boxes,
    title: '资源市场',
    text: '整合 Modrinth、CurseForge 和本地资源，支持兼容性验证和依赖安装。',
  },
  {
    icon: PackageOpen,
    title: '整合包工作流',
    text: '支持整合包导入导出、本地实例管理和版本独立配置。',
  },
  {
    icon: Cable,
    title: 'P2P 联机',
    text: '通过轻量 TCP 中继转发 Minecraft Java 版局域网流量，开房更直接。',
  },
];

const steps = [
  ['01', '下载 McGo.exe', '免安装启动，适合 Windows 桌面环境。'],
  ['02', '添加账号', '选择离线、Microsoft 或外置登录。'],
  ['03', '下载版本', '选择 Minecraft 版本和需要的加载器。'],
  ['04', '启动游戏', '选择账号与本地版本，进入你的世界。'],
];

function useActiveScreenshot() {
  const [active, setActive] = useState(0);

  useEffect(() => {
    const timer = window.setInterval(() => {
      setActive((value) => (value + 1) % screenshots.length);
    }, 3200);
    return () => window.clearInterval(timer);
  }, []);

  return [active, setActive];
}

function App() {
  const [active, setActive] = useActiveScreenshot();
  const current = screenshots[active];
  const downloadMeta = useMemo(
    () => ({
      href: '/download/McGo.exe',
      name: 'McGo.exe',
      size: '30.9 MB',
      platform: 'Windows',
    }),
    [],
  );

  return (
    <main className="page-shell">
      <nav className="topbar" aria-label="主导航">
        <a className="brand" href="#top">
          <img src="/assets/mcgo-icon.png" alt="" />
          <span>McGo</span>
        </a>
        <div className="nav-links">
          <a href="#features">功能</a>
          <a href="#gallery">截图</a>
          <a href="#download">下载</a>
        </div>
        <a className="nav-download" href={downloadMeta.href} download>
          <Download size={18} />
          <span>下载</span>
        </a>
      </nav>

      <section className="hero" id="top">
        <div className="hero-copy">
          <div className="eyebrow">
            <Sparkles size={16} />
            <span>现代 Minecraft 启动器</span>
          </div>
          <h1>McGo</h1>
          <p className="hero-lede">
            把账号、版本、加载器、资源市场和联机入口放进一个干净的桌面启动器里。
          </p>
          <div className="hero-actions">
            <a className="primary-action" href={downloadMeta.href} download>
              <ArrowDownToLine size={22} />
              <span>立即下载</span>
            </a>
            <a className="secondary-action" href="#features">
              <Play size={20} />
              <span>查看功能</span>
            </a>
          </div>
          <dl className="download-facts" aria-label="下载信息">
            <div>
              <dt>平台</dt>
              <dd>{downloadMeta.platform}</dd>
            </div>
            <div>
              <dt>文件</dt>
              <dd>{downloadMeta.name}</dd>
            </div>
            <div>
              <dt>大小</dt>
              <dd>{downloadMeta.size}</dd>
            </div>
          </dl>
        </div>

        <div className="hero-stage" aria-label="McGo 界面预览">
          <div className="pulse-ring ring-one" />
          <div className="pulse-ring ring-two" />
          <div className="screenshot-frame">
            <div className="window-bar">
              <span />
              <span />
              <span />
              <strong>{current.label}</strong>
            </div>
            <img src={current.src} alt={current.title} />
          </div>
          <div className="floating-panel panel-speed">
            <Zap size={18} />
            <span>多线程任务队列</span>
          </div>
          <div className="floating-panel panel-safe">
            <ShieldCheck size={18} />
            <span>启动前校验</span>
          </div>
        </div>
      </section>

      <section className="strip" aria-label="核心能力">
        <div>
          <strong>原版下载</strong>
          <span>版本清单和镜像源切换</span>
        </div>
        <div>
          <strong>加载器</strong>
          <span>Fabric / Forge / NeoForge / OptiFine</span>
        </div>
        <div>
          <strong>资源</strong>
          <span>Mod、资源包、光影、数据包</span>
        </div>
        <div>
          <strong>联机</strong>
          <span>P2P 隧道和中继服务</span>
        </div>
      </section>

      <section className="section feature-section" id="features">
        <div className="section-heading">
          <span className="section-kicker">功能介绍</span>
          <h2>从下载到开服前联机，常用动作都在一个窗口里完成</h2>
        </div>
        <div className="feature-grid">
          {features.map((feature) => {
            const Icon = feature.icon;
            return (
              <article className="feature-card" key={feature.title}>
                <div className="feature-icon">
                  <Icon size={23} />
                </div>
                <h3>{feature.title}</h3>
                <p>{feature.text}</p>
              </article>
            );
          })}
        </div>
      </section>

      <section className="section gallery-section" id="gallery">
        <div className="gallery-copy">
          <span className="section-kicker">界面截图</span>
          <h2>真实界面自动化截图生成</h2>
          <p>
            页面展示的截图来自项目运行界面，覆盖首页、启动、下载、资源市场、账号和联机页面。
          </p>
          <div className="gallery-tabs" role="tablist" aria-label="截图切换">
            {screenshots.map((shot, index) => (
              <button
                className={index === active ? 'active' : ''}
                type="button"
                role="tab"
                aria-selected={index === active}
                key={shot.label}
                onClick={() => setActive(index)}
              >
                {shot.label}
              </button>
            ))}
          </div>
        </div>
        <div className="gallery-preview">
          <div className="gallery-glow" />
          <img src={current.src} alt={current.title} />
          <div className="caption">
            <BadgeCheck size={18} />
            <span>{current.title}</span>
          </div>
        </div>
      </section>

      <section className="section workflow-section">
        <div className="section-heading">
          <span className="section-kicker">开始使用</span>
          <h2>四步进入游戏</h2>
        </div>
        <div className="steps">
          {steps.map(([index, title, text]) => (
            <article className="step" key={title}>
              <span className="step-index">{index}</span>
              <h3>{title}</h3>
              <p>{text}</p>
              <ChevronRight size={18} />
            </article>
          ))}
        </div>
      </section>

      <section className="download-section" id="download">
        <div className="download-copy">
          <span className="section-kicker">下载</span>
          <h2>获取 McGo 桌面版</h2>
          <p>当前页面内置 Windows 单文件构建产物，下载后可直接运行。</p>
        </div>
        <div className="download-box">
          <div className="download-icon">
            <MonitorDown size={34} />
          </div>
          <div>
            <strong>{downloadMeta.name}</strong>
            <span>{downloadMeta.platform} · {downloadMeta.size}</span>
          </div>
          <a className="primary-action compact" href={downloadMeta.href} download>
            <Download size={20} />
            <span>下载</span>
          </a>
        </div>
      </section>

      <footer className="footer">
        <div className="brand footer-brand">
          <img src="/assets/mcgo-icon.png" alt="" />
          <span>McGo</span>
        </div>
        <div className="footer-links">
          <span>
            <KeyRound size={16} />
            多账号
          </span>
          <span>
            <Search size={16} />
            资源搜索
          </span>
          <span>
            <Settings2 size={16} />
            版本设置
          </span>
          <span>
            <Gamepad2 size={16} />
            快速启动
          </span>
        </div>
      </footer>
    </main>
  );
}

createRoot(document.getElementById('root')).render(<App />);
