package rcat.bot;

import java.io.IOException;
import java.net.URI;
import java.util.Random;
import java.util.Timer;
import java.util.TimerTask;

import net.tootallnate.websocket.WebSocketClient;
import net.tootallnate.websocket.WebSocketDraft;


public class DumBot extends WebSocketClient {
	BotManager bm;
	int botId;

	public DumBot(BotManager bm, int num, long time, URI uri, WebSocketDraft draft) {
		super(uri, draft);
		this.bm = bm;
		this.botId = num;
		//System.out.println("Bot created.");
	}

	@Override
	public void send(String msg) {
		try {
			super.send(msg);
		} catch (IOException e) {
			// TODO Auto-generated catch block
			e.printStackTrace();
		}
	}
	@Override
	public void onMessage(String strmsg) {
		//non-logging => nothing to do when I receive that msg
	}

	@Override
	public void onOpen() {
		bm.notifyConnected();
		System.out.println("Bot "+ botId +" connected.");
	}

	@Override
	public void onClose() {
		System.out.println("Bot "+ botId +" disconnected.");
	}

	/**
	 * manages what the websocket client connections and msg sent
	 * @author tho
	 *
	 */
	public static class BotManager {
		public static final long serialVersionUID = -6056260699202978657L;

		public DumBot cc;
		int top;
		int left;
		Random r;
		int posToGoTo = 1; //bots always go down
		TimerTask doUpdatePosAndSend;
		TimerTask doClose;
		Timer timer;
		Timer ctimer;
		int numMsgSent;
		int botid; 

		public BotManager(final int num, long time, final URI uri, final int numMsg) {

			//this.r = new Random(Thread.currentThread().getId() + Config.MACHINE_SEED);
			//this.top = r.nextInt(Config.MAX_TOP);
			//this.left = r.nextInt(Config.MAX_LEFT);

			this.top = 0; //0 is just a hardcoded value so that I can see bots popping on the screen and disappear later on
			this.left = Config.MACHINE_SEED + num;

			// connect
			cc = new DumBot(this, num, time, uri, WebSocketDraft.DRAFT76);
			cc.connect();
			this.botid = num;

			// send	task
			doUpdatePosAndSend = new TimerTask() {
				@Override
				public void run() {
					updatePos();
					cc.send(getStrFromPos());
					numMsgSent ++;
					if(numMsgSent >= numMsg) { // I'm done sending all msg 
						timer.cancel();
						ctimer = new Timer();
						ctimer.schedule(doClose, num*Config.SLEEP_CLOSE);
					}
				}
			};
			// close task
			doClose = new TimerTask() {
				@Override
				public void run() {
					try {
						cc.close();
						ctimer.cancel();
					} catch (IOException e) {
						// TODO Auto-generated catch block
						e.printStackTrace();
					}
				}
			};
		} 		//end of botmanager constructor

		/**
		 * called by bothandler when connection has been made
		 * then manager can start sending msg
		 */
		public void notifyConnected() {
			this.timer = new Timer();
			this.numMsgSent = -2; //yeah thats weird, but otherwise the log says it took us 900ms to send 1000ms of msg ...
			this.timer.scheduleAtFixedRate(doUpdatePosAndSend, Config.SLEEP_START, (int) (1000/Config.FREQ));
		}

		/**
		 * return a string of the current bot position 
		 */
		private String getStrFromPos() {
			// 666 is just a dummy number, we dont use z for now
			return("{\"t\":" + this.top + ",\"l\":" + this.left + ",\"z\":" + 666 + "}"); 
		}

		/**
		 * update bot's current position (either up or down)
		 */
		private void updatePos() {
			/*
			if(this.top <= 0)
				posToGoTo = 1;
			if(this.top >= Config.MAX_TOP)
				posToGoTo = 0;
			// actually move
			switch(posToGoTo) {
			case 0: //up
				this.top = this.top - Config.TOP_SHIFT;
				break;
			case 1: //down
				this.top = this.top + Config.TOP_SHIFT;
				break;
			default:
				System.out.println("Error in switch case of posupdate");
			}
			 */
			this.top = this.top + Config.TOP_SHIFT; //bot keeps going down
		}

		/**
		 * print in stdout the thread/bot id and time since bot creation
		 * @param msg the message to print
		 */


	}
	//end of public class BotHandler2 


}
