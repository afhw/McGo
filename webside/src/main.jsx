import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Archive,
  ArrowDownToLine,
  BadgeCheck,
  Boxes,
  Cable,
  Check,
  ChevronRight,
  Code2,
  Download,
  Eye,
  FlaskConical,
  Gamepad2,
  Gauge,
  Github,
  HardDriveDownload,
  KeyRound,
  Layers3,
  Monitor,
  PackageOpen,
  Search,
  Settings2,
  ShieldCheck,
  UserRoundCheck,
  Users,
  Workflow,
} from 'lucide-react';
import './styles.css';

const previewDownload = {
  href: '/download/McGo-CSharp-Preview-x64-Setup.exe',
  name: 'McGo-CSharp-Preview-x64-Setup.exe',
  size: '81.4 MB',
  version: '1.0.12 Preview',
};

const stableDownload = {
  href: '/download/McGo.exe',
  name: 'McGo.exe',
  size: '30.9 MB',
  version: 'Python 稳定版',
};

const screenshots = [
  { file: '01-home.png', label: '首页', title: '实例、账号与运行环境集中呈现' },
  { file: '02-launch.png', label: '启动', title: '版本选择、启动参数与状态检查' },
  { file: '03-download.png', label: '下载', title: '原版、加载器与全局任务队列' },
  { file: '04-resources.png', label: '资源', title: 'Modrinth 资源搜索与依赖安装' },
  { file: '05-accounts.png', label: '账号', title: '离线、Microsoft 与外置登录' },
  { file: '06-online.png', label: '联机', title: 'P2P 房间、NAT 检测与胶囊补包' },
];

const screenshotUrl = (size, file) => `/screenshots/${size}/${file}`;

function ResponsiveScreenshot({ screenshot, className, alt }) {
  return (
    <picture className={`${className}-picture`}>
      <source media="(max-width: 700px)" srcSet={screenshotUrl('820x720', screenshot.file)} />
      <source media="(min-width: 1500px)" srcSet={screenshotUrl('1920x1080', screenshot.file)} />
      <img className={className} src={screenshotUrl('1440x900', screenshot.file)} alt={alt} />
    </picture>
  );
}

const features = [
  {
    icon: HardDriveDownload,
    title: '下载与补全',
    text: '原版文件、资源、Java 和缺失依赖进入同一个全局任务队列。',
  },
  {
    icon: Layers3,
    title: '加载器工作流',
    text: '连续安装 Fabric、Forge、NeoForge、OptiFine 与 Fabric API。',
  },
  {
    icon: UserRoundCheck,
    title: '账号与启动',
    text: '离线、Microsoft、外置登录与版本级启动设置统一管理。',
  },
  {
    icon: Boxes,
    title: '资源管理',
    text: '搜索 Modrinth 项目，检查版本兼容性并补齐必需前置。',
  },
  {
    icon: PackageOpen,
    title: '整合包与胶囊',
    text: '导入导出实例，并通过轻量胶囊同步开黑所需文件。',
  },
  {
    icon: Cable,
    title: 'P2P 联机',
    text: 'NAT 检测、TCP/UDP 隧道和中继回退组成更短的联机流程。',
  },
];

const previewHighlights = [
  'C# / .NET 8 / WinUI 3 原生客户端',
  '自研 WinUI 3 安装器，支持升级与卸载',
  'x64 自包含运行环境，仅简体中文资源',
  '全局下载任务与统一日志已迁移',
];

function useScreenshotRotation() {
  const [active, setActive] = useState(0);

  useEffect(() => {
    const timer = window.setInterval(() => {
      setActive((value) => (value + 1) % screenshots.length);
    }, 5000);
    return () => window.clearInterval(timer);
  }, []);

  return [active, setActive];
}

function DownloadButton({ download, children, className = '' }) {
  return (
    <a className={`download-button ${className}`.trim()} href={download.href} download>
      <ArrowDownToLine size={20} />
      <span>{children}</span>
    </a>
  );
}

function usePublicStats() {
  const [stats, setStats] = useState(null);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const response = await fetch(`/stats.json?ts=${Date.now()}`, { cache: 'no-store' });
        if (!response.ok) throw new Error(`Stats request failed: ${response.status}`);
        const data = await response.json();
        if (!cancelled) setStats(data);
      } catch {
        if (!cancelled) setStats(null);
      }
    };

    load();
    const timer = window.setInterval(load, 5 * 60 * 1000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, []);

  return stats;
}

function StatValue({ value }) {
  return Number.isFinite(value) ? new Intl.NumberFormat('zh-CN').format(value) : '--';
}

function App() {
  const [active, setActive] = useScreenshotRotation();
  const stats = usePublicStats();
  const current = screenshots[active];

  return (
    <main id="top">
      <nav className="topbar" aria-label="主导航">
        <a className="brand" href="#top" aria-label="McGo 首页">
          <img src="/assets/mcgo-icon.png" alt="" />
          <span>McGo</span>
        </a>
        <div className="nav-links">
          <a href="#product">产品</a>
          <a href="#gallery">界面</a>
          <a href="#download">下载</a>
        </div>
        <DownloadButton download={previewDownload} className="nav-download">
          C# 测试版
        </DownloadButton>
      </nav>

      <section className="hero" aria-labelledby="hero-title">
        <ResponsiveScreenshot screenshot={screenshots[0]} className="hero-image" alt="McGo C# WinUI 3 首页界面" />
        <div className="hero-shade" />
        <div className="hero-content">
          <div className="preview-label"><FlaskConical size={16} /> C# 测试版现已提供</div>
          <h1 id="hero-title">McGo</h1>
          <p>面向 Minecraft Java 版的 Windows 启动器。账号、版本、资源和联机工具都回到一个安静、直接的桌面工作区。</p>
          <div className="hero-actions">
            <DownloadButton download={previewDownload}>下载 C# 测试版</DownloadButton>
            <a className="secondary-button" href="#gallery">
              <Monitor size={20} />
              <span>查看 WinUI 界面</span>
            </a>
          </div>
          <div className="hero-meta" aria-label="测试版信息">
            <span><Code2 size={16} /> WinUI 3</span>
            <span><Monitor size={16} /> Windows x64</span>
            <span><Archive size={16} /> {previewDownload.size}</span>
          </div>
        </div>
      </section>

      <section className="release-bar" aria-label="版本状态">
        <div><BadgeCheck size={20} /><strong>稳定版继续保留</strong><span>现有 Python 单文件版本仍可下载</span></div>
        <div><FlaskConical size={20} /><strong>C# 迁移测试</strong><span>WinUI 3 功能与布局持续回归</span></div>
        <a href="https://github.com/afhw/McGo/tree/codex/winui3-csharp-migration" target="_blank" rel="noreferrer">
          <Github size={20} /><span>查看迁移分支</span><ChevronRight size={17} />
        </a>
      </section>

      <section className="product-section" id="product">
        <div className="section-intro">
          <span className="kicker">当前迁移范围</span>
          <h2>旧版常用能力，正在以 C# 重新落地</h2>
          <p>测试版已经覆盖启动、下载、账号、资源、整合包、联机、设置和日志。测试状态会明确标出，不与稳定版混淆。</p>
        </div>
        <div className="feature-list">
          {features.map((feature) => {
            const Icon = feature.icon;
            return (
              <article className="feature-row" key={feature.title}>
                <Icon size={23} />
                <div><h3>{feature.title}</h3><p>{feature.text}</p></div>
              </article>
            );
          })}
        </div>
      </section>

      <section className="gallery-section" id="gallery">
        <div className="gallery-header">
          <div>
            <span className="kicker">真实 C# WinUI 3 截图</span>
            <h2>{current.title}</h2>
          </div>
          <div className="gallery-tabs" role="tablist" aria-label="界面截图">
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
        <figure className="gallery-frame">
          <ResponsiveScreenshot screenshot={current} className="gallery-image" alt={current.title} />
          <figcaption><BadgeCheck size={17} /> 由当前 C# 客户端自动生成，按视口提供 820 / 1440 / 1920 三种宽度</figcaption>
        </figure>
      </section>

      <section className="download-section" id="download">
        <div className="download-heading">
          <span className="kicker">Windows 下载</span>
          <h2>选择适合你的版本</h2>
          <p>C# 版本通过 WinUI 3 安装器部署；需要免安装体验时，继续使用稳定版。</p>
          <div className="public-stats" aria-label="公开使用统计">
            <div><Eye size={18} /><strong><StatValue value={stats?.page_views} /></strong><span>页面访问</span></div>
            <div><Users size={18} /><strong><StatValue value={stats?.unique_visitors} /></strong><span>独立来源</span></div>
            <div><Download size={18} /><strong><StatValue value={stats?.downloads?.total} /></strong><span>下载请求</span></div>
          </div>
          <p className="stats-caption">
            匿名日志汇总，每 5 分钟更新
            {Number.isFinite(stats?.downloads?.preview) && ` · C# 安装器 ${new Intl.NumberFormat('zh-CN').format(stats.downloads.preview)} 次`}
          </p>
        </div>
        <div className="release-grid">
          <article className="release-card preview-release">
            <div className="release-card-head">
              <span className="release-icon"><FlaskConical size={25} /></span>
              <span className="status-badge">测试版</span>
            </div>
            <h3>C# / WinUI 3</h3>
            <p className="release-version">{previewDownload.version}</p>
            <ul>
              {previewHighlights.map((item) => <li key={item}><Check size={17} />{item}</li>)}
            </ul>
            <div className="file-meta"><span>{previewDownload.name}</span><strong>{previewDownload.size}</strong></div>
            <DownloadButton download={previewDownload}>下载 C# 测试版</DownloadButton>
            <a className="checksum-link" href="/download/McGo-CSharp-Preview-x64-Setup.exe.sha256" download>下载 SHA-256 校验文件</a>
          </article>

          <article className="release-card stable-release">
            <div className="release-card-head">
              <span className="release-icon"><ShieldCheck size={25} /></span>
              <span className="status-badge stable">稳定版</span>
            </div>
            <h3>经典单文件版</h3>
            <p className="release-version">{stableDownload.version}</p>
            <ul>
              <li><Check size={17} />单个 EXE，下载后直接运行</li>
              <li><Check size={17} />保留原有 Python 版成熟工作流</li>
              <li><Check size={17} />适合暂不参与迁移测试的用户</li>
              <li><Check size={17} />Windows x64 桌面环境</li>
            </ul>
            <div className="file-meta"><span>{stableDownload.name}</span><strong>{stableDownload.size}</strong></div>
            <DownloadButton download={stableDownload} className="stable-button">下载稳定版</DownloadButton>
          </article>
          <p className="signing-note">
            <ShieldCheck size={19} />
            <span>
              测试版本可能尚未进行代码签名。为确保下载完整、未被篡改，
              请在运行前核对文件的 SHA-256 校验值是否与发布页公布的一致。
            </span>
          </p>
        </div>
      </section>

      <section className="start-section" aria-labelledby="start-title">
        <div>
          <span className="kicker">开始测试</span>
          <h2 id="start-title">安装，启动，反馈</h2>
        </div>
        <ol className="start-steps">
          <li><span>01</span><div><strong>运行安装程序</strong><p>选择安装位置和是否创建桌面快捷方式。</p></div></li>
          <li><span>02</span><div><strong>启动 McGo</strong><p>首次启动后配置 Java 与游戏目录。</p></div></li>
          <li><span>03</span><div><strong>提交迁移反馈</strong><p>附上日志页内容和复现步骤。</p></div></li>
        </ol>
      </section>

      <footer className="footer">
        <div className="brand footer-brand"><img src="/assets/mcgo-icon.png" alt="" /><span>McGo</span></div>
        <div className="footer-capabilities">
          <span><Gauge size={16} />全局任务</span>
          <span><KeyRound size={16} />多账号</span>
          <span><Search size={16} />资源搜索</span>
          <span><Workflow size={16} />P2P 联机</span>
          <span><Settings2 size={16} />版本设置</span>
          <span><Gamepad2 size={16} />快速启动</span>
          <a href="/privacy.html">隐私政策</a>
        </div>
      </footer>
    </main>
  );
}

createRoot(document.getElementById('root')).render(<App />);
