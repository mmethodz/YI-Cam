"""OpenYI: standalone Windows camera viewer and recording library."""
from dataclasses import asdict
from datetime import datetime
import json
import logging
import os
from pathlib import Path
import queue
import shutil
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from PIL import Image, ImageTk

from .credentials import load_device, save_device
from .storage import GIB, Library, StoragePolicy, save_settings
from .worker import CameraWorker
from .platform_support import open_path

ROOT = Path(__file__).resolve().parents[1]
SETTINGS = ROOT / '.local' / 'settings.json'
BG, PANEL, TEXT, MUTED = '#101722', '#172232', '#f0f4fa', '#a6b7cf'
QUALITY = {'HD': 1, 'SD': 2, 'Auto': 0}
INFRARED = {'Infrared · auto in darkness': 0, 'Colour · extra lighting': 1, 'Automatic lighting': 2}


class RecorderApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title('OpenYI — Camera & Recordings')
        self.geometry('1180x800')
        self.minsize(980, 720)
        self.configure(bg=BG)
        try:
            settings = json.loads(SETTINGS.read_text(encoding='utf-8'))
        except (OSError, ValueError):
            settings = {}
        policy = settings.get('storage', {})
        self.events = queue.Queue()
        self.worker = CameraWorker(self.events)
        self.connected = self.recording = self.closing = self.connecting = False
        self.exporting = False
        self.photo = None
        self.folder = tk.StringVar(value=settings.get('folder', str(Path.home() / 'Videos' / 'YI Local')))
        self.status = tk.StringVar(value='Ready. Connect directly to your camera on the local network.')
        self.metric = tk.StringVar(value='No live stream')
        self.details = tk.StringVar(value='Original camera video · Local MP4 files · Video only')
        self.firmware = tk.StringVar(value='')
        self.quality = tk.StringVar(value='HD')
        self.infrared = tk.StringVar(value='Infrared · auto in darkness')
        self.tracking = tk.BooleanVar(value=False)
        self.quota = tk.StringVar(value=str(policy.get('quota_gib', 20)))
        self.minimum_free = tk.StringVar(value=str(policy.get('minimum_free_gib', 2)))
        self.segment = tk.StringVar(value=str(policy.get('segment_minutes', 10)))
        self.days = tk.StringVar(value=str(policy.get('keep_days', 0)))
        self.recycle = tk.BooleanVar(value=policy.get('recycle', False))
        self.disk_status = tk.StringVar(value='')
        try:
            device = load_device()
            paired = 'A saved pairing key is available for this Windows account.'
        except Exception:
            device, paired = {}, 'Add an existing device key or import it once from YI IOT.'
        self.ip = tk.StringVar(value=device.get('ip', ''))
        self.camera_name = tk.StringVar(value=device.get('name', 'YI Camera'))
        self.pair_status = tk.StringVar(value=paired)
        self.controls, self.storage_widgets = [], []
        self._build()
        self.worker.start()
        self.after(50, self.refresh)
        self.after(100, self.refresh_library)
        self.protocol('WM_DELETE_WINDOW', self.close_app)

    def label(self, parent, text=None, variable=None, **kwargs):
        options = dict(bg=PANEL, fg=TEXT, font=('Segoe UI', 10), anchor='w')
        options.update(kwargs)
        options['textvariable' if variable is not None else 'text'] = variable if variable is not None else text
        return tk.Label(parent, **options)

    def button(self, parent, text, callback, control=False, **kwargs):
        button = ttk.Button(parent, text=text, command=callback, **kwargs)
        if control:
            button.configure(state='disabled')
            self.controls.append(button)
        return button

    def _build(self):
        style = ttk.Style(self)
        style.theme_use('clam')
        style.configure('TButton', padding=(10, 8), font=('Segoe UI', 10))
        style.configure('Accent.TButton', background='#287cdb', foreground='white')
        style.configure('TNotebook', background=BG, borderwidth=0)
        style.configure('TNotebook.Tab', padding=(20, 10), font=('Segoe UI', 10))
        style.configure('TCombobox', padding=5)
        style.configure('Treeview', rowheight=29, font=('Segoe UI', 10))
        style.configure('Treeview.Heading', font=('Segoe UI', 10, 'bold'))
        head = tk.Frame(self, bg=BG)
        head.pack(fill='x', padx=24, pady=(18, 12))
        self.label(head, 'OpenYI', bg=BG, font=('Segoe UI', 25, 'bold')).pack(side='left')
        self.label(head, 'Your camera. Your recordings.', bg=BG, fg=MUTED).pack(side='left', padx=20)
        self.connect_button = self.button(head, 'Connect camera', self.connect, style='Accent.TButton')
        self.connect_button.pack(side='right')
        self.tabs = ttk.Notebook(self)
        self.tabs.pack(fill='both', expand=True, padx=24)
        live, library, storage, setup = [tk.Frame(self.tabs, bg=PANEL) for _ in range(4)]
        for panel, title in zip((live, library, storage, setup), ('Live camera', 'Recordings', 'Storage', 'Camera setup')):
            self.tabs.add(panel, text=title)
        self.tabs.bind('<<NotebookTabChanged>>', lambda _: self.refresh_library())
        self._live(live)
        self._library(library)
        self._storage(storage)
        self._setup(setup)
        self.label(self, variable=self.status, bg=BG, fg=MUTED, wraplength=1110, justify='left').pack(fill='x', padx=24, pady=(12, 16))

    def _live(self, panel):
        panel.columnconfigure(0, weight=1)
        panel.columnconfigure(1, weight=0, minsize=250)
        panel.rowconfigure(0, weight=1)
        left = tk.Frame(panel, bg=PANEL)
        left.grid(row=0, column=0, sticky='nsew', padx=(12, 8), pady=12)
        left.pack_propagate(False)
        self.preview = tk.Label(left, text='Connect your camera to view live video', width=1, height=1,
                               bg='#070d15', fg=MUTED, font=('Segoe UI', 13))
        self.preview.pack(fill='both', expand=True)
        self.label(left, variable=self.metric, font=('Segoe UI', 15, 'bold')).pack(fill='x', pady=(14, 5))
        self.label(left, variable=self.details, fg=MUTED, wraplength=740, justify='left').pack(fill='x')
        right = tk.Frame(panel, bg=PANEL, width=226)
        right.grid(row=0, column=1, sticky='ns', padx=(8, 16), pady=12)
        right.pack_propagate(False)
        for title, variable, choices, text, action in [
            ('STREAM QUALITY', self.quality, QUALITY, 'Apply quality', lambda: self.send('quality', QUALITY[self.quality.get()])),
            ('NIGHT VISION', self.infrared, INFRARED, 'Apply night vision', self.apply_infrared)]:
            self.label(right, title, fg=MUTED, font=('Segoe UI', 9, 'bold')).pack(fill='x', pady=(8, 0))
            ttk.Combobox(right, textvariable=variable, values=list(choices), state='readonly').pack(fill='x', pady=(6, 5))
            self.button(right, text, action, True).pack(fill='x')
        tracking = ttk.Checkbutton(right, text='Motion tracking', variable=self.tracking,
            command=lambda: self.send('tracking', self.tracking.get()), state='disabled')
        tracking.pack(anchor='w', pady=(15, 12))
        self.controls.append(tracking)
        arrows = tk.Frame(right, bg=PANEL)
        arrows.pack(pady=(0, 8))
        for text, direction, row, col in [('↑', 1, 0, 1), ('←', 3, 1, 0), ('→', 4, 1, 2), ('↓', 2, 2, 1)]:
            self.button(arrows, text, lambda d=direction: self.send('move', d), True, width=3).grid(row=row, column=col, padx=2, pady=2)
        self.button(arrows, '■', lambda: self.send('halt'), True, width=3).grid(row=1, column=1, padx=2, pady=2)
        self.label(right, 'Click an arrow for a short move.', fg=MUTED, font=('Segoe UI', 9)).pack(anchor='w')
        self.record_button = self.button(right, 'Start recording', self.toggle_recording, True, style='Accent.TButton')
        self.record_button.pack(fill='x', pady=(17, 8))
        self.button(right, 'Refresh camera settings', lambda: self.send('refresh_settings'), True).pack(fill='x')
        self.label(right, variable=self.firmware, fg=MUTED, wraplength=220, justify='left', font=('Segoe UI', 9)).pack(fill='x', pady=(12, 0))

    def _library(self, panel):
        bar = tk.Frame(panel, bg=PANEL)
        bar.pack(fill='x', padx=16, pady=16)
        for text, action in [('Refresh', self.refresh_library), ('Play', self.play_clip), ('Protect / unprotect', self.protect_clip),
            ('Export MP4', self.export_clip), ('Export 4K (upscaled)', lambda: self.export_clip(True)), ('Delete', self.delete_clip)]:
            self.button(bar, text, action).pack(side='left', padx=(0, 7))
        self.clips = ttk.Treeview(panel, columns=('started', 'duration', 'resolution', 'size', 'state'), show='headings', selectmode='browse')
        for name, title, width in [('started', 'Recorded', 230), ('duration', 'Length', 90), ('resolution', 'Resolution', 150), ('size', 'Size', 120), ('state', 'Status', 200)]:
            self.clips.heading(name, text=title)
            self.clips.column(name, width=width)
        self.clips.pack(fill='both', expand=True, padx=16)
        self.clips.bind('<Double-1>', lambda _: self.play_clip())
        self.label(panel, 'Protected clips are kept when recycling. 4K export scales the image; it adds no captured detail.', fg=MUTED, wraplength=1050).pack(fill='x', padx=16, pady=(12, 6))
        self.label(panel, variable=self.disk_status, fg=MUTED).pack(fill='x', padx=16, pady=(0, 16))

    def _storage(self, panel):
        content = tk.Frame(panel, bg=PANEL)
        content.pack(fill='x', padx=24, pady=24)
        self.label(content, 'Recording storage', font=('Segoe UI', 18, 'bold')).grid(row=0, column=0, columnspan=3, sticky='w', pady=(0, 20))
        self.label(content, 'Folder').grid(row=1, column=0, sticky='w', pady=8)
        entry = ttk.Entry(content, textvariable=self.folder, width=62)
        entry.grid(row=1, column=1, sticky='ew', padx=18)
        browse = self.button(content, 'Choose…', self.choose_folder)
        browse.grid(row=1, column=2)
        self.storage_widgets += [entry, browse]
        for row, title, variable, help_text in [(2, 'Recording budget (GiB)', self.quota, 'Space for this app’s library'),
            (3, 'Keep disk free (GiB)', self.minimum_free, 'Stop before using this reserve'),
            (4, 'Clip length (minutes)', self.segment, 'Rotate at the next keyframe'),
            (5, 'Maximum age (days)', self.days, '0 = unlimited; applies when recycling is on')]:
            self.label(content, title).grid(row=row, column=0, sticky='w', pady=10)
            box = ttk.Entry(content, textvariable=variable, width=12)
            box.grid(row=row, column=1, sticky='w', padx=18)
            self.storage_widgets.append(box)
            self.label(content, help_text, fg=MUTED).grid(row=row, column=1, columnspan=2, sticky='w', padx=(160, 0))
        recycle = ttk.Checkbutton(content, text='Recycle the oldest unprotected recordings when limits are reached', variable=self.recycle)
        recycle.grid(row=6, column=0, columnspan=3, sticky='w', pady=(20, 10))
        self.storage_widgets.append(recycle)
        self.label(content, 'With recycling off, recording stops at the limit. Only clips created by this app are managed.', fg=MUTED, wraplength=920).grid(row=7, column=0, columnspan=3, sticky='w', pady=(0, 18))
        save = self.button(content, 'Save storage settings', self.save_storage, style='Accent.TButton')
        save.grid(row=8, column=0, sticky='w')
        self.storage_widgets.append(save)
        self.button(content, 'Open recording folder', self.open_folder).grid(row=8, column=1, sticky='w', padx=18)
        self.label(content, 'Windows stays awake during recording. Closing the app stops recording.', fg=MUTED).grid(row=9, column=0, columnspan=3, sticky='w', pady=24)

    def _setup(self, panel):
        content = tk.Frame(panel, bg=PANEL)
        content.pack(fill='x', padx=24, pady=24)
        self.label(content, 'Local camera pairing', font=('Segoe UI', 18, 'bold')).pack(anchor='w')
        self.label(content, 'The saved device key authenticates directly with the camera. No account login is used.', fg=MUTED).pack(anchor='w', pady=(8, 22))
        for title, variable in [('Camera name', self.camera_name), ('Camera IPv4 address on this network', self.ip)]:
            self.label(content, title).pack(anchor='w')
            ttk.Entry(content, textvariable=variable, width=45).pack(anchor='w', pady=(5, 14))
        self.button(content, 'Save name / address', self.save_camera_address).pack(anchor='w')
        self.label(content, variable=self.pair_status, fg=MUTED, wraplength=970).pack(anchor='w', pady=(14, 24))
        self.label(content, 'Add an existing 15-character device key', font=('Segoe UI', 11, 'bold')).pack(anchor='w')
        self.key_entry = ttk.Entry(content, show='•', width=45)
        self.key_entry.pack(anchor='w', pady=(7, 8))
        self.button(content, 'Verify and save key', self.save_key).pack(anchor='w')
        self.button(content, 'Import key from the open YI IOT live view', self.import_key).pack(anchor='w', pady=(20, 6))
        self.label(content, 'Import supports the tested 2022 Windows client and needs optional Frida. After import, YI IOT can stay closed.\n'
            'Keys are encrypted for your Windows account. QR setup for a reset camera is still under investigation.',
            fg=MUTED, wraplength=950, justify='left').pack(anchor='w', pady=8)

    def send(self, name, value=None):
        self.worker.commands.put((name, value))

    def policy(self):
        return StoragePolicy(float(self.quota.get()), float(self.minimum_free.get()), float(self.segment.get()), self.recycle.get(), int(self.days.get())).validate()

    def save_storage(self):
        try:
            save_settings(SETTINGS, {'folder': str(Path(self.folder.get()).expanduser().resolve()), 'storage': asdict(self.policy())})
            self.status.set('Storage settings saved.')
            self.refresh_library()
        except Exception as exc:
            self.status.set(str(exc))

    def connect(self):
        if self.connected or self.connecting or self.worker.wanted:
            self.connecting = False
            self.send('disconnect')
        else:
            self.connecting = True
            self.connect_button.configure(text='Cancel connection')
            self.send('connect')

    def toggle_recording(self):
        try:
            self.send('stop') if self.recording else self.send('record', (self.folder.get(), asdict(self.policy())))
        except Exception as exc:
            self.status.set(str(exc))

    def apply_infrared(self):
        if self.infrared.get() in INFRARED:
            self.send('infrared', INFRARED[self.infrared.get()])

    def choose_folder(self):
        folder = filedialog.askdirectory(parent=self, initialdir=self.folder.get(), title='Recording library folder')
        if folder:
            self.folder.set(folder)

    def open_folder(self):
        path = Path(self.folder.get()).expanduser().resolve()
        path.mkdir(parents=True, exist_ok=True)
        open_path(path)

    def refresh_library(self):
        if not hasattr(self, 'clips'):
            return
        library = None
        try:
            library = Library(self.folder.get())
            rows = library.clips()
            selected = self.clips.selection()
            self.clips.delete(*self.clips.get_children())
            for row in rows:
                state = 'Protected' if row['protected'] else 'Saved' if row['complete'] else 'Recording / incomplete'
                if not row['exists']:
                    state = 'File missing'
                length = f"{int(row['duration'])//60}:{int(row['duration'])%60:02d}"
                self.clips.insert('', 'end', iid=row['name'], values=(datetime.fromtimestamp(row['started']).strftime('%Y-%m-%d %H:%M:%S'),
                    length, f"{row['width']} × {row['height']}", f"{row['bytes']/1024**2:.1f} MB", state))
            if selected and self.clips.exists(selected[0]):
                self.clips.selection_set(selected[0])
            total, free = sum(row['bytes'] for row in rows)/GIB, shutil.disk_usage(library.root).free/GIB
            self.disk_status.set(f'{len(rows)} clips · {total:.2f} GiB recorded · {free:.1f} GiB disk free')
        except Exception as exc:
            self.status.set(str(exc))
        finally:
            if library:
                library.close()

    def selected_clip(self):
        selected = self.clips.selection()
        if not selected:
            self.status.set('Select a recording first.')
        return selected[0] if selected else None

    def play_clip(self):
        name = self.selected_clip()
        if name:
            library = Library(self.folder.get())
            try:
                open_path(library.path(name))
            except Exception as exc:
                self.status.set(str(exc))
            finally:
                library.close()

    def protect_clip(self):
        if self.exporting:
            self.status.set('Wait for the export to finish before changing clip protection.')
            return
        name = self.selected_clip()
        if name:
            library = Library(self.folder.get())
            try:
                row = next(row for row in library.clips() if row['name'] == name)
                library.protect(name, not row['protected'])
            finally:
                library.close()
            self.refresh_library()

    def delete_clip(self):
        if self.exporting:
            self.status.set('Wait for the export to finish before deleting clips.')
            return
        name = self.selected_clip()
        if name and messagebox.askyesno('Delete recording', 'Permanently delete this recording?\n\n'+name, parent=self):
            library = Library(self.folder.get())
            try:
                library.delete(name)
            except Exception as exc:
                self.status.set(str(exc))
            finally:
                library.close()
            self.refresh_library()

    def export_clip(self, upscale=False):
        if self.exporting:
            self.status.set('An export is already running.')
            return
        name = self.selected_clip()
        if not name:
            return
        library = Library(self.folder.get())
        try:
            source = library.path(name)
            row = next(row for row in library.clips() if row['name'] == name)
            if not row['complete']:
                self.status.set('Wait for the clip to finish before exporting.')
                return
        finally:
            library.close()
        target = filedialog.asksaveasfilename(parent=self, title='Export recording', initialfile=source.stem+('_4K_upscaled.mp4' if upscale else '_export.mp4'),
            defaultextension='.mp4', filetypes=[('MP4 video', '*.mp4')])
        if not target:
            return
        export_folder = self.folder.get()
        self.exporting = True
        def export():
            export_library = None
            try:
                export_library = Library(export_folder)
                export_library.protect(name, True)
                from .media import export_video
                export_video(source, Path(target), upscale)
                self.events.put({'kind': 'status', 'message': 'Export saved: '+target})
            except Exception as exc:
                self.events.put({'kind': 'error', 'message': 'Export failed: '+str(exc)})
            finally:
                if export_library:
                    try:
                        export_library.protect(name, bool(row['protected']))
                    finally:
                        export_library.close()
                self.events.put({'kind': 'export_finished'})
        self.status.set('Exporting 4K upscale…' if upscale else 'Exporting original recording…')
        threading.Thread(target=export, daemon=True).start()

    def save_camera_address(self):
        if self.connected or self.worker.wanted:
            self.status.set('Disconnect before changing camera setup.')
            return
        try:
            from .protocol import Camera
            device = load_device()
            Camera(self.ip.get().strip(), device['password'], device.get('uid'))
            device.update(ip=self.ip.get().strip(), name=self.camera_name.get().strip() or 'YI Camera')
            save_device(device)
            self.pair_status.set('Name and address saved. Existing pairing key retained.')
        except Exception as exc:
            self.status.set(str(exc))

    def save_key(self):
        if self.connected or self.worker.wanted:
            self.status.set('Disconnect before changing camera setup.')
            return
        key = self.key_entry.get()
        self.key_entry.delete(0, 'end')
        ip, name = self.ip.get().strip(), self.camera_name.get().strip() or 'YI Camera'
        def verify():
            try:
                from .protocol import Camera
                with Camera(ip, key) as camera:
                    camera.firmware()
                    save_device({'ip': ip, 'name': name, 'password': key, 'uid': camera.uid})
                self.events.put({'kind': 'paired', 'message': 'Device key verified and saved for this Windows account.'})
            except Exception as exc:
                self.events.put({'kind': 'error', 'message': str(exc)})
        threading.Thread(target=verify, daemon=True).start()

    def import_key(self):
        if self.connected or self.worker.wanted:
            self.status.set('Disconnect before importing a pairing key.')
            return
        self.status.set('Importing from the open YI IOT live view…')
        self.send('import', (self.ip.get().strip(), self.camera_name.get().strip() or 'YI Camera'))

    def refresh(self):
        preview = None
        for _ in range(200):
            try:
                event = self.events.get_nowait()
            except queue.Empty:
                break
            kind = event['kind']
            if kind == 'closed' and self.closing:
                self.destroy()
                return
            if kind == 'preview':
                preview = event['image']
            elif kind == 'export_finished':
                self.exporting = False
                self.refresh_library()
            elif kind in ('status', 'error'):
                self.status.set(event['message'])
            elif kind == 'paired':
                self.pair_status.set(event['message'])
                self.status.set(event['message'])
            elif kind == 'firmware':
                self.firmware.set('Firmware '+event['version'])
            elif kind == 'settings':
                self.infrared.set(next((name for name, mode in INFRARED.items() if mode == event['infrared']), f"Camera mode {event['infrared']}"))
                self.tracking.set(event['tracking'] == 1)
            elif kind == 'connected':
                self.connected = event['active']
                self.connecting = False
                self.connect_button.configure(text='Disconnect' if self.connected else 'Cancel reconnect' if self.worker.wanted else 'Connect camera')
                for button in self.controls:
                    button.configure(state='normal' if self.connected else 'disabled')
                if self.recording:
                    self.record_button.configure(state='normal')
                if not self.connected:
                    self.preview.configure(image='', text='Disconnected — retrying…' if self.worker.wanted else 'Camera disconnected')
                    self.metric.set('No live stream')
            elif kind == 'recording':
                self.recording = event['active']
                self.record_button.configure(text='Stop recording' if self.recording else 'Start recording')
                for widget in self.storage_widgets:
                    widget.configure(state='disabled' if self.recording else 'normal')
                self.status.set(event['message'])
                self.refresh_library()
            elif kind == 'stats':
                if self.connected:
                    self.metric.set(f"{event['width']} × {event['height']}   ·   {event['fps']:.1f} fps")
                self.details.set(f"REC   {event['frames']:,} frames · {event['mb']:.1f} MB · {event['recycled']} clips recycled\n{event['file'] or 'Waiting for a keyframe…'}" if self.recording
                    else 'Original camera video · Local MP4 files · Video only')
        if preview is not None and self.connected:
            preview.thumbnail((max(1, self.preview.winfo_width()), max(1, self.preview.winfo_height())), Image.Resampling.BILINEAR)
            self.photo = ImageTk.PhotoImage(preview)
            self.preview.configure(image=self.photo, text='')
        self.after(50, self.refresh)

    def close_app(self):
        if self.exporting:
            self.status.set('Wait for the export to finish before closing.')
            return
        if not self.closing:
            self.closing = True
            self.status.set('Finishing the recording and disconnecting…')
            self.send('quit')


def main():
    SETTINGS.parent.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(filename=SETTINGS.parent / 'app.log', level=logging.INFO,
                        format='%(asctime)s %(message)s', encoding='utf-8')
    RecorderApp().mainloop()


if __name__ == '__main__':
    main()
